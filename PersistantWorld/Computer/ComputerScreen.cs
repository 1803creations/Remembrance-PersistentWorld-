using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using Rage;
using Rage.Native;
using PersistentWorld.Database;
using System.IO;
using System.Linq;

namespace PersistentWorld.Computer
{
    public class ComputerScreen
    {
        private DatabaseManager _database;
        private bool _isOpen = false;
        private bool _gameFrozen = false;

        // UI state
        private enum ScreenMode { Search, TicketMenu }
        private ScreenMode _currentScreen = ScreenMode.Search;

        private enum SearchMode { Vehicle, Person }
        private SearchMode _currentSearchMode = SearchMode.Vehicle;

        // Input fields for search
        private StringBuilder _vehiclePlateInput = new StringBuilder();
        private StringBuilder _personFirstNameInput = new StringBuilder();
        private StringBuilder _personLastNameInput = new StringBuilder();

        // Which field is active (for person lookup)
        private enum PersonField { FirstName, LastName }
        private PersonField _activePersonField = PersonField.FirstName;

        // Results
        private List<string> _leftColumnResults = new List<string>();
        private List<string> _rightColumnResults = new List<string>();
        private List<Dictionary<string, object>> _lastPersonResults = null;
        private int _selectedResultIndex = -1;

        // Ticket menu state
        private List<TicketTemplate> _ticketTemplates = new List<TicketTemplate>();
        private List<TicketTemplate> _arrestTemplates = new List<TicketTemplate>();
        private int _selectedTicketIndex = 0;
        private int _ticketMenuScrollOffset = 0;
        private const int MAX_VISIBLE_TICKETS = 10;
        private Dictionary<string, object> _currentSelectedPerson = null;
        private StringBuilder _citationLocation = new StringBuilder();
        private bool _showingArrests = false;

        // Quick search suggestions
        private List<string> _quickSuggestions = new List<string>();
        private int _selectedSuggestionIndex = -1;
        private bool _showSuggestions = false;

        // Cursor blinking
        private bool _cursorVisible = true;
        private DateTime _lastCursorBlink = DateTime.Now;

        // Input debounce
        private DateTime _lastKeyPress = DateTime.Now;
        private DateTime _lastControllerInput = DateTime.Now;

        // Config path
        private string _configPath;

        // Person suggestions loaded from database
        private List<PersonSuggestion> _personSuggestions = new List<PersonSuggestion>();

        // VEHICLE SUGGESTIONS - Loaded from database
        private List<VehicleSuggestion> _vehicleSuggestions = new List<VehicleSuggestion>();

        private class PersonSuggestion
        {
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public string FullName { get; set; }
            public string DisplayName { get; set; }
        }

        // Vehicle suggestion class
        private class VehicleSuggestion
        {
            public string LicensePlate { get; set; }
            public string Model { get; set; }
            public string OwnerName { get; set; }
            public string DisplayName { get; set; }
        }

        // Ticket template class
        private class TicketTemplate
        {
            public string Title { get; set; }
            public string Description { get; set; }
            public int Fine { get; set; }
            public string Code { get; set; }
            public bool IsArrestable { get; set; }
            public int JailDays { get; set; }

            public string DisplayText
            {
                get
                {
                    if (IsArrestable)
                        return $"{Title} - {JailDays} days";
                    else
                        return $"{Title} - ${Fine}";
                }
            }
        }

        public ComputerScreen(DatabaseManager database)
        {
            _database = database;

            // Get GTA V directory and build path to INI
            string gtaPath = AppDomain.CurrentDomain.BaseDirectory;
            _configPath = Path.Combine(gtaPath, "Plugins", "LSPDFR", "PersistentWorld", "citations.ini");

            LoadTicketsFromConfig();

            // Load all suggestions
            LoadPersonSuggestions();
            LoadVehicleSuggestions();
        }

        private void LoadPersonSuggestions()
        {
            try
            {
                Game.LogTrivial("[Computer] Loading person suggestions from database...");

                var allPeds = _database.LookupByName("", "");

                if (allPeds != null)
                {
                    Game.LogTrivial($"[Computer] Database returned {allPeds.Count} peds");

                    if (allPeds.Count > 0)
                    {
                        _personSuggestions.Clear();
                        foreach (var ped in allPeds)
                        {
                            string firstName = ped["first_name"]?.ToString() ?? "";
                            string lastName = ped["last_name"]?.ToString() ?? "";
                            string fullName = $"{firstName} {lastName}";
                            string employer = ped.ContainsKey("employer_name") ? ped["employer_name"]?.ToString() : "";
                            string displayName = string.IsNullOrEmpty(employer) ? fullName : $"{fullName} - {employer}";

                            _personSuggestions.Add(new PersonSuggestion
                            {
                                FirstName = firstName.ToLower(),
                                LastName = lastName.ToLower(),
                                FullName = fullName.ToLower(),
                                DisplayName = displayName
                            });
                        }
                        Game.LogTrivial($"[Computer] Successfully loaded {_personSuggestions.Count} person suggestions");
                    }
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[Computer] ERROR loading person suggestions: {ex.Message}");
            }
        }

        // Load vehicle suggestions from database
        private void LoadVehicleSuggestions()
        {
            try
            {
                Game.LogTrivial("[Computer] Loading vehicle suggestions from database...");

                var allPeds = _database.LookupByName("", "");

                if (allPeds != null)
                {
                    _vehicleSuggestions.Clear();
                    foreach (var ped in allPeds)
                    {
                        // Check if person has vehicles
                        if (ped.ContainsKey("owned_vehicles"))
                        {
                            var vehicles = ped["owned_vehicles"] as List<Dictionary<string, object>>;
                            if (vehicles != null)
                            {
                                foreach (var vehicle in vehicles)
                                {
                                    string plate = vehicle.ContainsKey("license_plate") ? vehicle["license_plate"].ToString() : "";
                                    string model = vehicle.ContainsKey("vehicle_model") ? vehicle["vehicle_model"].ToString() : "";
                                    string ownerName = $"{ped["first_name"]} {ped["last_name"]}";

                                    if (!string.IsNullOrEmpty(plate))
                                    {
                                        _vehicleSuggestions.Add(new VehicleSuggestion
                                        {
                                            LicensePlate = plate.ToUpper(),
                                            Model = model,
                                            OwnerName = ownerName,
                                            DisplayName = $"{plate} - {model} ({ownerName})"
                                        });
                                    }
                                }
                            }
                        }
                    }
                    Game.LogTrivial($"[Computer] Loaded {_vehicleSuggestions.Count} vehicle suggestions");
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[Computer] ERROR loading vehicle suggestions: {ex.Message}");
            }
        }

        private void LoadTicketsFromConfig()
        {
            _ticketTemplates.Clear();
            _arrestTemplates.Clear();

            if (!File.Exists(_configPath))
            {
                Game.LogTrivial("citations.ini not found, creating default");
                CreateDefaultConfig();
            }

            try
            {
                string[] lines = File.ReadAllLines(_configPath);
                string currentSection = "";

                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith(";") || trimmedLine.StartsWith("#") || string.IsNullOrEmpty(trimmedLine))
                        continue;

                    if (trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
                    {
                        currentSection = trimmedLine.Substring(1, trimmedLine.Length - 2).ToUpper();
                        continue;
                    }

                    var ticket = ParseTicketLine(trimmedLine, currentSection);
                    if (ticket != null)
                    {
                        if (currentSection == "FELONIES_ARRESTABLE")
                        {
                            ticket.IsArrestable = true;
                            _arrestTemplates.Add(ticket);
                        }
                        else
                        {
                            ticket.IsArrestable = false;
                            _ticketTemplates.Add(ticket);
                        }
                    }
                }

                _ticketTemplates.Sort((a, b) => a.Fine.CompareTo(b.Fine));
                _arrestTemplates.Sort((a, b) => a.JailDays.CompareTo(b.JailDays));

                Game.LogTrivial($"Loaded {_ticketTemplates.Count} citations and {_arrestTemplates.Count} arrestable offenses");
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"Error loading citations.ini: {ex.Message}");
                LoadDefaultTickets();
            }
        }

        private TicketTemplate ParseTicketLine(string line, string section)
        {
            try
            {
                string[] parts = line.Split('|');
                if (parts.Length < 4)
                    return null;

                string title = parts[0].Trim();
                string description = parts[1].Trim();
                int fine = int.Parse(parts[2].Trim());
                string code = parts[3].Trim();
                int jailDays = 0;

                if (parts.Length >= 5)
                {
                    jailDays = int.Parse(parts[4].Trim());
                }

                return new TicketTemplate
                {
                    Title = title,
                    Description = description,
                    Fine = fine,
                    Code = code,
                    JailDays = jailDays
                };
            }
            catch
            {
                return null;
            }
        }

        private void CreateDefaultConfig()
        {
            var defaultLines = new List<string>
            {
                "; Persistent World Citations Configuration",
                "; Format: Title | Description | Fine | Code",
                "; Lines starting with ; are comments",
                "",
                "[MOVING_VIOLATIONS]",
                "Speeding (1-10 mph) | Speeding 1-10 mph over limit | 150 | VC 22350",
                "Speeding (11-15 mph) | Speeding 11-15 mph over limit | 250 | VC 22350",
                "Speeding (16-25 mph) | Speeding 16-25 mph over limit | 350 | VC 22350",
                "Speeding (25+ mph) | Speeding 25+ mph over limit | 500 | VC 22350",
                "Reckless Driving | Reckless driving endangerment | 500 | VC 23103",
                "Running Red Light | Failed to stop at red light | 350 | VC 21453",
                "Running Stop Sign | Failed to stop at stop sign | 250 | VC 22450",
                "Illegal U-Turn | Illegal U-turn intersection | 200 | VC 22102",
                "Texting While Driving | Using phone while driving | 200 | VC 23123",
                "Seat Belt Violation | Driver not wearing seat belt | 150 | VC 27315",
                "",
                "[EQUIPMENT_VIOLATIONS]",
                "Broken Tail Light | Broken or malfunctioning tail light | 100 | VC 24600",
                "Broken Headlight | Broken or malfunctioning headlight | 100 | VC 24250",
                "Illegal Tint | Window tint too dark | 150 | VC 26708",
                "Loud Exhaust | Modified/illegal exhaust | 200 | VC 27150",
                "Suspended Registration | Expired tags over 6 months | 250 | VC 4000",
                "",
                "[LICENSE_VIOLATIONS]",
                "No Insurance | Driving without insurance | 800 | VC 16028",
                "Expired License | Driver's license expired | 250 | VC 12500",
                "No License | Driving without a license | 300 | VC 12500",
                "Suspended License | Driving on suspended license | 500 | VC 14601",
                "",
                "[PARKING_VIOLATIONS]",
                "Parking - Fire Lane | Parked in fire lane | 150 | PMC 87",
                "Parking - Handicap | Parked in handicap space | 350 | PMC 225",
                "Parking - Red Zone | Parked in red zone | 120 | PMC 88",
                "Parking - Street Sweeping | Parked during street sweeping | 80 | PMC 80",
                "",
                "[OTHER_VIOLATIONS]",
                "Open Container | Alcohol in vehicle | 250 | VC 23222",
                "Littering | Throwing trash from vehicle | 200 | PMC 65",
                "",
                "[FELONIES_ARRESTABLE]",
                "DUI | Driving under influence | 1500 | VC 23152 | 30",
                "DUI with Injury | DUI causing injury | 5000 | VC 23153 | 180",
                "Hit and Run | Leaving scene of accident | 2000 | VC 20002 | 60",
                "Hit and Run with Injury | Leaving scene with injury | 5000 | VC 20001 | 180",
                "Grand Theft Auto | Stolen vehicle | 5000 | PC 487 | 120",
                "Assault with Deadly Weapon | Assault with weapon | 5000 | PC 245 | 180",
                "Drug Possession | Possession of controlled substance | 1000 | HS 11350 | 60",
                "Carrying Concealed Weapon | CCW without permit | 2000 | PC 25400 | 90",
                "Felony Evading | Evading police in vehicle | 3000 | VC 2800.2 | 120"
            };

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath));
                File.WriteAllLines(_configPath, defaultLines);
                Game.LogTrivial("Created default citations.ini");
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"Failed to create default config: {ex.Message}");
            }
        }

        private void LoadDefaultTickets()
        {
            _ticketTemplates.Clear();
            _arrestTemplates.Clear();

            _ticketTemplates.Add(new TicketTemplate { Title = "Speeding (1-10 mph)", Description = "Speeding - 1-10 mph over limit", Fine = 150, Code = "VC 22350" });
            _ticketTemplates.Add(new TicketTemplate { Title = "Speeding (11-15 mph)", Description = "Speeding - 11-15 mph over limit", Fine = 250, Code = "VC 22350" });
            _ticketTemplates.Add(new TicketTemplate { Title = "Speeding (16-25 mph)", Description = "Speeding - 16-25 mph over limit", Fine = 350, Code = "VC 22350" });
            _ticketTemplates.Add(new TicketTemplate { Title = "Speeding (25+ mph)", Description = "Speeding - 25+ mph over limit", Fine = 500, Code = "VC 22350" });
            _ticketTemplates.Add(new TicketTemplate { Title = "Reckless Driving", Description = "Reckless driving", Fine = 500, Code = "VC 23103" });
            _ticketTemplates.Add(new TicketTemplate { Title = "Running Red Light", Description = "Failed to stop at red light", Fine = 350, Code = "VC 21453" });
            _ticketTemplates.Add(new TicketTemplate { Title = "Broken Tail Light", Description = "Broken or malfunctioning tail light", Fine = 100, Code = "VC 24600" });
            _ticketTemplates.Add(new TicketTemplate { Title = "Broken Headlight", Description = "Broken or malfunctioning headlight", Fine = 100, Code = "VC 24250" });
            _ticketTemplates.Add(new TicketTemplate { Title = "Illegal Tint", Description = "Window tint too dark", Fine = 150, Code = "VC 26708" });
            _ticketTemplates.Add(new TicketTemplate { Title = "No Insurance", Description = "Driving without insurance", Fine = 800, Code = "VC 16028" });
            _ticketTemplates.Add(new TicketTemplate { Title = "Expired License", Description = "Driver's license expired", Fine = 250, Code = "VC 12500" });
            _ticketTemplates.Add(new TicketTemplate { Title = "Suspended License", Description = "Driving on suspended license", Fine = 500, Code = "VC 14601" });

            _arrestTemplates.Add(new TicketTemplate { Title = "DUI", Description = "Driving under influence", Fine = 1500, Code = "VC 23152", JailDays = 30, IsArrestable = true });
            _arrestTemplates.Add(new TicketTemplate { Title = "Hit and Run", Description = "Leaving scene of accident", Fine = 2000, Code = "VC 20002", JailDays = 60, IsArrestable = true });
            _arrestTemplates.Add(new TicketTemplate { Title = "Grand Theft Auto", Description = "Stolen vehicle", Fine = 5000, Code = "PC 487", JailDays = 120, IsArrestable = true });
        }

        public void Show()
        {
            if (_isOpen) return;

            _isOpen = true;
            ResetUI();

            Game.LogTrivial("Computer opened");
            Game.DisplayNotification("Computer ~g~opened");

            // Reload suggestions every time computer opens (in case database changed)
            LoadPersonSuggestions();
            LoadVehicleSuggestions();

            // Freeze the game completely
            Game.LocalPlayer.Character.IsInvincible = true;
            Game.LocalPlayer.Character.KeepTasks = true;
            Game.LocalPlayer.Character.BlockPermanentEvents = true;

            NativeFunction.Natives.FREEZE_ENTITY_POSITION(Game.LocalPlayer.Character, true);
            Game.TimeScale = 0.0f;
            NativeFunction.Natives.SET_PLAYER_CONTROL(Game.LocalPlayer, false, 0);
            NativeFunction.Natives.DISPLAY_HUD(false);
            NativeFunction.Natives.DISPLAY_RADAR(false);

            // Disable specific problematic controls
            NativeFunction.Natives.DISABLE_CONTROL_ACTION(0, 303, true); // B button (backup menu)
            NativeFunction.Natives.DISABLE_CONTROL_ACTION(0, 202, true); // B button
            NativeFunction.Natives.DISABLE_CONTROL_ACTION(0, 168, true); // F6
            NativeFunction.Natives.DISABLE_CONTROL_ACTION(0, 169, true); // F7

            _gameFrozen = true;

            int debugCounter = 0;
            while (_isOpen)
            {
                GameFiber.Yield();

                // Keep game frozen every frame
                if (_gameFrozen)
                {
                    Game.TimeScale = 0.0f;
                    NativeFunction.Natives.FREEZE_ENTITY_POSITION(Game.LocalPlayer.Character, true);
                    NativeFunction.Natives.SET_PLAYER_CONTROL(Game.LocalPlayer, false, 0);

                    NativeFunction.Natives.DISABLE_CONTROL_ACTION(0, 303, true);
                    NativeFunction.Natives.DISABLE_CONTROL_ACTION(0, 202, true);
                    NativeFunction.Natives.DISABLE_CONTROL_ACTION(0, 168, true);
                    NativeFunction.Natives.DISABLE_CONTROL_ACTION(0, 169, true);
                }

                HandleInput();
                HandleControllerInput();

                // Update suggestions every frame
                UpdateSuggestions();

                // Log debug info every 60 frames
                debugCounter++;
                if (debugCounter >= 60)
                {
                    debugCounter = 0;
                    Game.LogTrivial($"[Computer] Current mode: {_currentSearchMode}, Person suggestions: {_personSuggestions.Count}, Vehicle suggestions: {_vehicleSuggestions.Count}");
                }

                if ((DateTime.Now - _lastCursorBlink).TotalMilliseconds > 500)
                {
                    _cursorVisible = !_cursorVisible;
                    _lastCursorBlink = DateTime.Now;
                }

                DrawComputerScreen();
            }
        }

        private void ResetUI()
        {
            _vehiclePlateInput.Clear();
            _personFirstNameInput.Clear();
            _personLastNameInput.Clear();
            _leftColumnResults.Clear();
            _rightColumnResults.Clear();
            _quickSuggestions.Clear();
            _selectedSuggestionIndex = -1;
            _showSuggestions = false;
            _currentScreen = ScreenMode.Search;
            _currentSearchMode = SearchMode.Vehicle;
            _activePersonField = PersonField.FirstName;
            _selectedResultIndex = -1;
            _lastPersonResults = null;
            _currentSelectedPerson = null;
            _selectedTicketIndex = 0;
            _ticketMenuScrollOffset = 0;
            _citationLocation.Clear();
            _showingArrests = false;
        }

        private void UpdateSuggestions()
        {
            _quickSuggestions.Clear();

            if (_currentSearchMode == SearchMode.Vehicle)
            {
                string input = _vehiclePlateInput.ToString().ToUpper();

                if (input.Length >= 1)
                {
                    // Search vehicles from database suggestions
                    foreach (var vehicle in _vehicleSuggestions)
                    {
                        if (vehicle.LicensePlate.Contains(input) ||
                            vehicle.Model.ToUpper().Contains(input) ||
                            vehicle.OwnerName.ToUpper().Contains(input))
                        {
                            _quickSuggestions.Add(vehicle.DisplayName);
                        }
                    }
                }
                else
                {
                    // Show some recent/sample vehicles when no input
                    foreach (var vehicle in _vehicleSuggestions.Take(10))
                    {
                        _quickSuggestions.Add(vehicle.DisplayName);
                    }
                }
            }
            else // Person mode
            {
                string firstName = _personFirstNameInput.ToString().ToLower();
                string lastName = _personLastNameInput.ToString().ToLower();

                if (_personSuggestions.Count == 0)
                {
                    LoadPersonSuggestions();
                }

                // If no input, show all suggestions
                if (firstName.Length == 0 && lastName.Length == 0)
                {
                    foreach (var person in _personSuggestions.Take(20))
                    {
                        _quickSuggestions.Add(person.DisplayName);
                    }
                }
                else
                {
                    // Filter based on input
                    foreach (var person in _personSuggestions)
                    {
                        bool match = false;

                        if (firstName.Length > 0 && lastName.Length > 0)
                        {
                            match = person.FirstName.Contains(firstName) && person.LastName.Contains(lastName);
                        }
                        else if (firstName.Length > 0)
                        {
                            match = person.FirstName.StartsWith(firstName);
                        }
                        else if (lastName.Length > 0)
                        {
                            match = person.LastName.StartsWith(lastName);
                        }

                        if (match)
                        {
                            _quickSuggestions.Add(person.DisplayName);
                        }
                    }
                }
            }

            // Remove duplicates
            _quickSuggestions = _quickSuggestions.Distinct().ToList();

            // Show suggestions if we have any
            bool hadSuggestions = _showSuggestions;
            _showSuggestions = _quickSuggestions.Count > 0;

            if (_showSuggestions)
            {
                if (!hadSuggestions)
                    _selectedSuggestionIndex = 0;
                else if (_selectedSuggestionIndex >= _quickSuggestions.Count)
                    _selectedSuggestionIndex = _quickSuggestions.Count - 1;
            }
            else
            {
                _selectedSuggestionIndex = -1;
            }
        }

        private void HandleInput()
        {
            if ((DateTime.Now - _lastKeyPress).TotalMilliseconds < 100)
                return;

            if (Game.IsKeyDown(Keys.Escape))
            {
                if (_currentScreen == ScreenMode.TicketMenu)
                {
                    _currentScreen = ScreenMode.Search;
                    _lastKeyPress = DateTime.Now;
                }
                else
                {
                    Close();
                }
                return;
            }

            if (_currentScreen == ScreenMode.Search)
            {
                HandleSearchInput();
            }
            else if (_currentScreen == ScreenMode.TicketMenu)
            {
                HandleTicketMenuInput();
            }
        }

        private void HandleControllerInput()
        {
            if ((DateTime.Now - _lastControllerInput).TotalMilliseconds < 150)
                return;

            bool dpadDown = NativeFunction.Natives.IS_CONTROL_PRESSED<int>(0, 187) == 1;
            bool dpadUp = NativeFunction.Natives.IS_CONTROL_PRESSED<int>(0, 188) == 1;
            bool dpadLeft = NativeFunction.Natives.IS_CONTROL_PRESSED<int>(0, 189) == 1;
            bool dpadRight = NativeFunction.Natives.IS_CONTROL_PRESSED<int>(0, 190) == 1;
            bool aButton = NativeFunction.Natives.IS_CONTROL_PRESSED<int>(0, 201) == 1;
            bool bButton = NativeFunction.Natives.IS_CONTROL_PRESSED<int>(0, 202) == 1;
            bool xButton = NativeFunction.Natives.IS_CONTROL_PRESSED<int>(0, 203) == 1;
            bool yButton = NativeFunction.Natives.IS_CONTROL_PRESSED<int>(0, 204) == 1;

            if (bButton)
            {
                if (_currentScreen == ScreenMode.TicketMenu)
                {
                    _currentScreen = ScreenMode.Search;
                    _lastControllerInput = DateTime.Now;
                }
                else
                {
                    Close();
                    _lastControllerInput = DateTime.Now;
                }
                return;
            }

            if (_currentScreen == ScreenMode.Search)
            {
                // FIRST: Field switching for Person mode
                if (_currentSearchMode == SearchMode.Person)
                {
                    if (dpadUp || dpadDown)
                    {
                        _activePersonField = (_activePersonField == PersonField.FirstName) ?
                            PersonField.LastName : PersonField.FirstName;
                        _lastControllerInput = DateTime.Now;
                        return;
                    }
                }

                // THEN: Handle other navigation
                if (dpadUp)
                {
                    if (_showSuggestions)
                    {
                        _selectedSuggestionIndex--;
                        if (_selectedSuggestionIndex < 0)
                            _selectedSuggestionIndex = _quickSuggestions.Count - 1;
                    }
                    else if (_lastPersonResults != null && _lastPersonResults.Count > 1)
                    {
                        _selectedResultIndex--;
                        if (_selectedResultIndex < 0)
                            _selectedResultIndex = _lastPersonResults.Count - 1;
                        DisplaySelectedPerson();
                    }
                    _lastControllerInput = DateTime.Now;
                    return;
                }

                if (dpadDown)
                {
                    if (_showSuggestions)
                    {
                        _selectedSuggestionIndex++;
                        if (_selectedSuggestionIndex >= _quickSuggestions.Count)
                            _selectedSuggestionIndex = 0;
                    }
                    else if (_lastPersonResults != null && _lastPersonResults.Count > 1)
                    {
                        _selectedResultIndex++;
                        if (_selectedResultIndex >= _lastPersonResults.Count)
                            _selectedResultIndex = 0;
                        DisplaySelectedPerson();
                    }
                    _lastControllerInput = DateTime.Now;
                    return;
                }

                if (aButton)
                {
                    if (_showSuggestions && _selectedSuggestionIndex >= 0)
                    {
                        SelectSuggestion();
                    }
                    else
                    {
                        PerformSearch();
                    }
                    _lastControllerInput = DateTime.Now;
                    return;
                }

                if (xButton)
                {
                    SwitchMode();
                    _lastControllerInput = DateTime.Now;
                    return;
                }

                if (dpadLeft)
                {
                    HandleBackspace();
                    _lastControllerInput = DateTime.Now;
                    return;
                }
            }
            else if (_currentScreen == ScreenMode.TicketMenu)
            {
                if (dpadUp)
                {
                    _selectedTicketIndex--;
                    if (_selectedTicketIndex < 0)
                    {
                        List<TicketTemplate> currentList = _showingArrests ? _arrestTemplates : _ticketTemplates;
                        _selectedTicketIndex = currentList.Count - 1;
                    }

                    if (_selectedTicketIndex < _ticketMenuScrollOffset)
                        _ticketMenuScrollOffset = _selectedTicketIndex;

                    _lastControllerInput = DateTime.Now;
                    return;
                }

                if (dpadDown)
                {
                    _selectedTicketIndex++;
                    List<TicketTemplate> currentList = _showingArrests ? _arrestTemplates : _ticketTemplates;
                    if (_selectedTicketIndex >= currentList.Count)
                        _selectedTicketIndex = 0;

                    if (_selectedTicketIndex >= _ticketMenuScrollOffset + MAX_VISIBLE_TICKETS)
                        _ticketMenuScrollOffset = _selectedTicketIndex - MAX_VISIBLE_TICKETS + 1;

                    _lastControllerInput = DateTime.Now;
                    return;
                }

                if (aButton)
                {
                    IssueSelectedTicket();
                    _lastControllerInput = DateTime.Now;
                    return;
                }

                if (xButton)
                {
                    _showingArrests = !_showingArrests;
                    _selectedTicketIndex = 0;
                    _ticketMenuScrollOffset = 0;
                    _lastControllerInput = DateTime.Now;
                    return;
                }

                if (bButton)
                {
                    if (_citationLocation.Length > 0)
                    {
                        _citationLocation.Remove(_citationLocation.Length - 1, 1);
                    }
                    else
                    {
                        _currentScreen = ScreenMode.Search;
                    }
                    _lastControllerInput = DateTime.Now;
                    return;
                }
            }
        }

        private void HandleSearchInput()
        {
            if (Game.IsKeyDown(Keys.Tab))
            {
                SwitchMode();
                _lastKeyPress = DateTime.Now;
                return;
            }

            if (Game.IsKeyDown(Keys.F6) && _lastPersonResults != null && _lastPersonResults.Count > 0)
            {
                OpenTicketMenu();
                _lastKeyPress = DateTime.Now;
                return;
            }

            if (Game.IsKeyDown(Keys.Enter))
            {
                if (_showSuggestions && _selectedSuggestionIndex >= 0)
                {
                    SelectSuggestion();
                }
                else
                {
                    PerformSearch();
                }
                _lastKeyPress = DateTime.Now;

                if (_currentSearchMode == SearchMode.Person)
                {
                    _activePersonField = PersonField.FirstName;
                }
                return;
            }

            // Field switching for Person mode
            if (_currentSearchMode == SearchMode.Person)
            {
                if (Game.IsKeyDown(Keys.Up) || Game.IsKeyDown(Keys.Down))
                {
                    _activePersonField = (_activePersonField == PersonField.FirstName) ?
                        PersonField.LastName : PersonField.FirstName;
                    _lastKeyPress = DateTime.Now;
                    return;
                }
            }

            // Suggestion navigation
            if (_showSuggestions)
            {
                if (Game.IsKeyDown(Keys.Up))
                {
                    _selectedSuggestionIndex--;
                    if (_selectedSuggestionIndex < 0)
                        _selectedSuggestionIndex = _quickSuggestions.Count - 1;
                    _lastKeyPress = DateTime.Now;
                    return;
                }
                if (Game.IsKeyDown(Keys.Down))
                {
                    _selectedSuggestionIndex++;
                    if (_selectedSuggestionIndex >= _quickSuggestions.Count)
                        _selectedSuggestionIndex = 0;
                    _lastKeyPress = DateTime.Now;
                    return;
                }
            }
            else if (_lastPersonResults != null && _lastPersonResults.Count > 1)
            {
                if (Game.IsKeyDown(Keys.Up))
                {
                    _selectedResultIndex--;
                    if (_selectedResultIndex < 0)
                        _selectedResultIndex = _lastPersonResults.Count - 1;
                    DisplaySelectedPerson();
                    _lastKeyPress = DateTime.Now;
                    return;
                }
                if (Game.IsKeyDown(Keys.Down))
                {
                    _selectedResultIndex++;
                    if (_selectedResultIndex >= _lastPersonResults.Count)
                        _selectedResultIndex = 0;
                    DisplaySelectedPerson();
                    _lastKeyPress = DateTime.Now;
                    return;
                }
            }

            if (Game.IsKeyDown(Keys.Back))
            {
                HandleBackspace();
                _lastKeyPress = DateTime.Now;
                return;
            }

            // Letters A-Z
            for (Keys key = Keys.A; key <= Keys.Z; key++)
            {
                if (Game.IsKeyDown(key))
                {
                    char c = (char)('A' + (key - Keys.A));
                    HandleCharacter(c);
                    _lastKeyPress = DateTime.Now;
                    return;
                }
            }

            if (_currentSearchMode == SearchMode.Vehicle)
            {
                for (Keys key = Keys.D0; key <= Keys.D9; key++)
                {
                    if (Game.IsKeyDown(key))
                    {
                        char c = (char)('0' + (key - Keys.D0));
                        HandleCharacter(c);
                        _lastKeyPress = DateTime.Now;
                        return;
                    }
                }
            }

            if (_currentSearchMode == SearchMode.Person && Game.IsKeyDown(Keys.Space))
            {
                HandleCharacter(' ');
                _lastKeyPress = DateTime.Now;
                return;
            }
        }

        private void HandleTicketMenuInput()
        {
            if (Game.IsKeyDown(Keys.Tab))
            {
                _showingArrests = !_showingArrests;
                _selectedTicketIndex = 0;
                _ticketMenuScrollOffset = 0;
                _lastKeyPress = DateTime.Now;
                return;
            }

            List<TicketTemplate> currentList = _showingArrests ? _arrestTemplates : _ticketTemplates;
            if (currentList.Count == 0) return;

            if (Game.IsKeyDown(Keys.Up))
            {
                _selectedTicketIndex--;
                if (_selectedTicketIndex < 0)
                    _selectedTicketIndex = currentList.Count - 1;

                // Update scroll offset if needed
                if (_selectedTicketIndex < _ticketMenuScrollOffset)
                    _ticketMenuScrollOffset = _selectedTicketIndex;

                _lastKeyPress = DateTime.Now;
                return;
            }

            if (Game.IsKeyDown(Keys.Down))
            {
                _selectedTicketIndex++;
                if (_selectedTicketIndex >= currentList.Count)
                    _selectedTicketIndex = 0;

                // Update scroll offset if needed
                if (_selectedTicketIndex >= _ticketMenuScrollOffset + MAX_VISIBLE_TICKETS)
                    _ticketMenuScrollOffset = _selectedTicketIndex - MAX_VISIBLE_TICKETS + 1;

                _lastKeyPress = DateTime.Now;
                return;
            }

            if (Game.IsKeyDown(Keys.Enter))
            {
                IssueSelectedTicket();
                _lastKeyPress = DateTime.Now;
                return;
            }

            if (Game.IsKeyDown(Keys.Back))
            {
                if (_citationLocation.Length > 0)
                {
                    _citationLocation.Remove(_citationLocation.Length - 1, 1);
                }
                else
                {
                    _currentScreen = ScreenMode.Search;
                }
                _lastKeyPress = DateTime.Now;
                return;
            }

            for (Keys key = Keys.A; key <= Keys.Z; key++)
            {
                if (Game.IsKeyDown(key))
                {
                    char c = (char)('A' + (key - Keys.A));
                    if (_citationLocation.Length < 30)
                        _citationLocation.Append(c);
                    _lastKeyPress = DateTime.Now;
                    return;
                }
            }

            for (Keys key = Keys.D0; key <= Keys.D9; key++)
            {
                if (Game.IsKeyDown(key))
                {
                    char c = (char)('0' + (key - Keys.D0));
                    if (_citationLocation.Length < 30)
                        _citationLocation.Append(c);
                    _lastKeyPress = DateTime.Now;
                    return;
                }
            }

            if (Game.IsKeyDown(Keys.Space))
            {
                if (_citationLocation.Length < 30)
                    _citationLocation.Append(' ');
                _lastKeyPress = DateTime.Now;
                return;
            }
        }

        private void HandleCharacter(char c)
        {
            if (_currentSearchMode == SearchMode.Vehicle)
            {
                if (_vehiclePlateInput.Length < 8)
                    _vehiclePlateInput.Append(char.ToUpper(c));
            }
            else
            {
                StringBuilder target = _activePersonField == PersonField.FirstName ?
                    _personFirstNameInput : _personLastNameInput;

                if (target.Length < 20)
                {
                    if (target.Length == 0)
                        target.Append(char.ToUpper(c));
                    else
                        target.Append(char.ToLower(c));
                }
            }
        }

        private void HandleBackspace()
        {
            if (_currentSearchMode == SearchMode.Vehicle)
            {
                if (_vehiclePlateInput.Length > 0)
                    _vehiclePlateInput.Remove(_vehiclePlateInput.Length - 1, 1);
            }
            else
            {
                StringBuilder target = _activePersonField == PersonField.FirstName ?
                    _personFirstNameInput : _personLastNameInput;

                if (target.Length > 0)
                    target.Remove(target.Length - 1, 1);
            }
        }

        private void SelectSuggestion()
        {
            if (_selectedSuggestionIndex < 0 || _selectedSuggestionIndex >= _quickSuggestions.Count)
                return;

            string selected = _quickSuggestions[_selectedSuggestionIndex];

            if (_currentSearchMode == SearchMode.Vehicle)
            {
                // Extract plate from display name (format: "PLATE - Model (Owner)")
                string plate = selected.Split('-')[0].Trim();
                _vehiclePlateInput.Clear();
                _vehiclePlateInput.Append(plate);
                PerformSearch();
            }
            else
            {
                // Extract name from suggestion (format: "First Last - Employer" or just "First Last")
                string namePart = selected.Split('-')[0].Trim();
                int firstSpace = namePart.IndexOf(' ');
                if (firstSpace > 0)
                {
                    string firstName = namePart.Substring(0, firstSpace);
                    string lastName = namePart.Substring(firstSpace + 1);

                    _personFirstNameInput.Clear();
                    _personFirstNameInput.Append(firstName);
                    _personLastNameInput.Clear();
                    _personLastNameInput.Append(lastName);

                    PerformSearch();
                    _activePersonField = PersonField.FirstName;
                }
            }

            _quickSuggestions.Clear();
            _showSuggestions = false;
            _selectedSuggestionIndex = -1;
        }

        private void SwitchMode()
        {
            _currentSearchMode = _currentSearchMode == SearchMode.Vehicle ? SearchMode.Person : SearchMode.Vehicle;
            _leftColumnResults.Clear();
            _rightColumnResults.Clear();
            _quickSuggestions.Clear();
            _showSuggestions = false;
            _selectedSuggestionIndex = -1;
            _lastPersonResults = null;
            _selectedResultIndex = -1;
            _activePersonField = PersonField.FirstName;
            Game.DisplayNotification($"Switched to ~b~{_currentSearchMode}~w~ lookup");
        }

        private void PerformSearch()
        {
            _leftColumnResults.Clear();
            _rightColumnResults.Clear();
            _quickSuggestions.Clear();
            _showSuggestions = false;
            _selectedSuggestionIndex = -1;

            if (_currentSearchMode == SearchMode.Vehicle)
            {
                string plate = _vehiclePlateInput.ToString();
                if (string.IsNullOrEmpty(plate))
                {
                    _leftColumnResults.Add("~r~Please enter a license plate");
                    return;
                }

                Game.LogTrivial($"Searching for vehicle plate: {plate}");
                var result = _database.LookupByPlate(plate);

                if (result != null && result.Count > 0)
                {
                    _leftColumnResults.Add($"=== VEHICLE RESULTS ===");

                    // Vehicle info
                    string vehicleType = result.ContainsKey("owner_type") ? result["owner_type"].ToString() : "Unknown";
                    string ownerName = result.ContainsKey("owner_name") ? result["owner_name"].ToString() : "Unknown";

                    _leftColumnResults.Add($"Plate: {result["license_plate"]}");
                    _leftColumnResults.Add($"Model: {result["vehicle_model"]}");

                    if (result.ContainsKey("color_primary"))
                        _leftColumnResults.Add($"Color: {result["color_primary"]}/{result["color_secondary"]}");

                    _leftColumnResults.Add($"Owner: {ownerName} ({vehicleType})");

                    if (result.ContainsKey("company_name") && result["company_name"] != null)
                        _leftColumnResults.Add($"Company: {result["company_name"]}");

                    // NEW: Registration status
                    if (result.ContainsKey("no_registration") && Convert.ToInt32(result["no_registration"]) == 1)
                    {
                        _leftColumnResults.Add($"~r~NO REGISTRATION");
                    }
                    else if (result.ContainsKey("registration_expiry") && result["registration_expiry"] != null)
                    {
                        string regExpiry = result["registration_expiry"].ToString();
                        if (DateTime.TryParse(regExpiry, out DateTime expiryDate))
                        {
                            if (expiryDate < DateTime.Now)
                                _leftColumnResults.Add($"~y~Registration EXPIRED: {regExpiry}");
                            else
                                _leftColumnResults.Add($"~g~Registration Valid until: {regExpiry}");
                        }
                    }

                    // NEW: Insurance status
                    if (result.ContainsKey("no_insurance") && Convert.ToInt32(result["no_insurance"]) == 1)
                    {
                        _leftColumnResults.Add($"~r~NO INSURANCE");
                    }
                    else if (result.ContainsKey("insurance_expiry") && result["insurance_expiry"] != null)
                    {
                        string insExpiry = result["insurance_expiry"].ToString();
                        if (DateTime.TryParse(insExpiry, out DateTime expiryDate))
                        {
                            if (expiryDate < DateTime.Now)
                                _leftColumnResults.Add($"~y~Insurance EXPIRED: {insExpiry}");
                            else
                                _leftColumnResults.Add($"~g~Insurance Valid until: {insExpiry}");
                        }
                    }

                    // NEW: Stolen status
                    if (result.ContainsKey("is_stolen") && Convert.ToInt32(result["is_stolen"]) == 1)
                    {
                        string stolenReason = result.ContainsKey("stolen_reason") ? result["stolen_reason"].ToString() : "Unknown";
                        _leftColumnResults.Add($"~r~*** STOLEN VEHICLE ***");
                        _leftColumnResults.Add($"~r~Reason: {stolenReason}");
                    }

                    // NEW: Impounded status
                    if (result.ContainsKey("is_impounded") && Convert.ToInt32(result["is_impounded"]) == 1)
                    {
                        string impoundReason = result.ContainsKey("impounded_reason") ? result["impounded_reason"].ToString() : "Unknown";
                        string impoundLocation = result.ContainsKey("impounded_location") ? result["impounded_location"].ToString() : "Unknown";
                        _leftColumnResults.Add($"~r~*** IMPOUNDED ***");
                        _leftColumnResults.Add($"~r~Reason: {impoundReason}");
                        _leftColumnResults.Add($"~y~Location: {impoundLocation}");
                    }

                    // Check if owner is wanted
                    if (result.ContainsKey("is_wanted") && Convert.ToBoolean(result["is_wanted"]))
                    {
                        string wantedReason = result.ContainsKey("wanted_reason") ? result["wanted_reason"].ToString() : "Unknown";
                        _leftColumnResults.Add($"~r~OWNER IS WANTED: {wantedReason}");
                    }

                    // Check if owner is incarcerated
                    if (result.ContainsKey("is_incarcerated") && Convert.ToBoolean(result["is_incarcerated"]))
                    {
                        _leftColumnResults.Add($"~r~OWNER IS INCARCERATED");
                    }

                    // Get person info if owner is a person
                    if (result.ContainsKey("ped_id") && result["ped_id"] != null && Convert.ToInt32(result["ped_id"]) > 0)
                    {
                        int pedId = Convert.ToInt32(result["ped_id"]);
                        string firstName = result.ContainsKey("first_name") ? result["first_name"].ToString() : "";
                        string lastName = result.ContainsKey("last_name") ? result["last_name"].ToString() : "";

                        if (!string.IsNullOrEmpty(firstName) && !string.IsNullOrEmpty(lastName))
                        {
                            var personResults = _database.LookupByName(firstName, lastName);
                            if (personResults != null && personResults.Count > 0)
                            {
                                _lastPersonResults = personResults;
                                _selectedResultIndex = 0;
                                LoadCitationHistory(personResults[0]);
                            }
                        }
                    }
                }
                else
                {
                    _leftColumnResults.Add($"~r~No vehicle found with plate: {plate}");
                }
            }
            else // Person mode
            {
                string firstName = _personFirstNameInput.ToString();
                string lastName = _personLastNameInput.ToString();

                firstName = firstName.Trim();
                lastName = lastName.Trim();

                if (string.IsNullOrEmpty(firstName) && string.IsNullOrEmpty(lastName))
                {
                    _leftColumnResults.Add("~r~Please enter a first or last name");
                    return;
                }

                Game.LogTrivial($"Searching for person: '{firstName}' '{lastName}'");

                try
                {
                    var results = _database.LookupByName(firstName, lastName);
                    _lastPersonResults = results;

                    if (results != null && results.Count > 0)
                    {
                        _selectedResultIndex = 0;
                        DisplaySelectedPerson();
                    }
                    else
                    {
                        _leftColumnResults.Add("~y~No matching persons found");
                    }
                }
                catch (Exception ex)
                {
                    Game.LogTrivial($"[Computer] Error during person search: {ex.Message}");
                    _leftColumnResults.Add($"~r~Error during search: {ex.Message}");
                }
            }

            if (_currentSearchMode == SearchMode.Person)
            {
                _activePersonField = PersonField.FirstName;
            }
        }

        private void LoadCitationHistory(Dictionary<string, object> person)
        {
            _rightColumnResults.Clear();

            _rightColumnResults.Add($"=== CITATION HISTORY ===");
            _rightColumnResults.Add($"");

            if (person == null)
            {
                _rightColumnResults.Add("No person data available");
                return;
            }

            var tickets = person.ContainsKey("ticket_history") ?
                person["ticket_history"] as List<Dictionary<string, object>> : null;

            if (tickets != null && tickets.Count > 0)
            {
                foreach (var ticket in tickets)
                {
                    if (ticket == null) continue;

                    string date = ticket.ContainsKey("date_issued") && ticket["date_issued"] != null
                        ? ticket["date_issued"].ToString()
                        : "Unknown";

                    string offense = ticket.ContainsKey("offense") && ticket["offense"] != null
                        ? ticket["offense"].ToString()
                        : "Unknown";

                    string fine = ticket.ContainsKey("fine_amount") && ticket["fine_amount"] != null
                        ? ticket["fine_amount"].ToString()
                        : "0";

                    string vehicle = ticket.ContainsKey("license_plate") && ticket["license_plate"] != null
                        ? ticket["license_plate"].ToString()
                        : "Unknown";

                    string model = ticket.ContainsKey("vehicle_model") && ticket["vehicle_model"] != null
                        ? ticket["vehicle_model"].ToString()
                        : "";

                    if (offense.Length > 25)
                        offense = offense.Substring(0, 22) + "...";

                    _rightColumnResults.Add($"{date}:");
                    _rightColumnResults.Add($"  {offense}");

                    if (!string.IsNullOrEmpty(vehicle) && vehicle != "Unknown")
                        _rightColumnResults.Add($"  Vehicle: {vehicle} {model}");

                    _rightColumnResults.Add($"  ~y~${fine}");
                    _rightColumnResults.Add($"");
                }
            }
            else
            {
                _rightColumnResults.Add("No prior citations");
                _rightColumnResults.Add("");
                _rightColumnResults.Add("Clean driving record");
            }
        }

        private void DisplaySelectedPerson()
        {
            if (_lastPersonResults == null || _lastPersonResults.Count == 0 || _selectedResultIndex < 0)
                return;

            var person = _lastPersonResults[_selectedResultIndex];

            if (person == null) return;

            string firstName = person["first_name"].ToString();
            string lastName = person["last_name"].ToString();

            Game.LogTrivial($"[Computer] Displaying {firstName} {lastName}");

            // Safely refresh data
            try
            {
                var freshResults = _database.LookupByName(firstName, lastName);
                if (freshResults != null && freshResults.Count > 0)
                {
                    person = freshResults[0];
                    _lastPersonResults[_selectedResultIndex] = person;
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[Computer] Error refreshing data: {ex.Message}");
            }

            _currentSelectedPerson = person;

            _leftColumnResults.Clear();

            string countText = _lastPersonResults.Count > 1 ?
                $" (Result {_selectedResultIndex + 1} of {_lastPersonResults.Count})" : "";

            _leftColumnResults.Add($"=== PERSON INFO{countText} ===");
            _leftColumnResults.Add($"Name: {person["first_name"]} {person["last_name"]}");
            _leftColumnResults.Add($"Address: {person["home_address"]}");

            // License info from peds table
            string licenseNum = person.ContainsKey("license_number") ? person["license_number"].ToString() : "None";
            string licenseStatus = person.ContainsKey("license_status") ? person["license_status"].ToString() : "Valid";
            string licenseReason = person.ContainsKey("license_reason") ? person["license_reason"].ToString() : "";
            string licenseExpiry = person.ContainsKey("license_expiry") ? person["license_expiry"].ToString() : "";
            string licenseClass = person.ContainsKey("license_class") ? person["license_class"].ToString() : "Class C";

            string statusColor = "~g~";
            switch (licenseStatus.ToUpper())
            {
                case "SUSPENDED": statusColor = "~y~"; break;
                case "REVOKED": statusColor = "~r~"; break;
                case "EXPIRED": statusColor = "~y~"; break;
                case "NOLICENSE": case "NO LICENSE": statusColor = "~r~"; break;
            }

            _leftColumnResults.Add($"License: {licenseNum} {statusColor}{licenseStatus.ToUpper()}~w~");
            _leftColumnResults.Add($"Class: {licenseClass}");

            if (!string.IsNullOrEmpty(licenseExpiry))
            {
                if (DateTime.TryParse(licenseExpiry, out DateTime expiryDate))
                {
                    if (expiryDate < DateTime.Now)
                        _leftColumnResults.Add($"License EXPIRED: ~y~{licenseExpiry}~w~");
                    else
                        _leftColumnResults.Add($"Expires: {licenseExpiry}");
                }
                else
                {
                    _leftColumnResults.Add($"Expires: {licenseExpiry}");
                }
            }

            if (!string.IsNullOrEmpty(licenseReason) &&
                (licenseStatus.ToUpper() == "SUSPENDED" || licenseStatus.ToUpper() == "REVOKED"))
            {
                _leftColumnResults.Add($"Reason: {licenseReason}");
            }

            // WANTED STATUS
            bool isWanted = person.ContainsKey("is_wanted") ? Convert.ToBoolean(person["is_wanted"]) : false;
            if (isWanted)
            {
                string wantedReason = person.ContainsKey("wanted_reason") ? person["wanted_reason"].ToString() : "Unknown";
                _leftColumnResults.Add($"~r~*** WANTED ***");
                _leftColumnResults.Add($"~r~Reason: {wantedReason}");

                if (person.ContainsKey("wanted_last_seen") && person["wanted_last_seen"] != null && person["wanted_last_seen"].ToString() != "")
                {
                    _leftColumnResults.Add($"~y~Last Seen: {person["wanted_last_seen"]}");
                }
                _leftColumnResults.Add($"");
            }

            // INCARCERATED STATUS
            bool isIncarcerated = person.ContainsKey("is_incarcerated") ? Convert.ToBoolean(person["is_incarcerated"]) : false;
            if (isIncarcerated)
            {
                string incarceratedReason = person.ContainsKey("incarcerated_reason") ? person["incarcerated_reason"].ToString() : "Unknown";
                int days = person.ContainsKey("incarcerated_days") ? Convert.ToInt32(person["incarcerated_days"]) : 0;
                string releaseDate = person.ContainsKey("release_date") ? person["release_date"].ToString() : "Unknown";

                _leftColumnResults.Add($"~r~*** INCARCERATED ***");
                _leftColumnResults.Add($"~r~Reason: {incarceratedReason}");
                _leftColumnResults.Add($"~y~Sentence: {days} days");
                _leftColumnResults.Add($"~y~Release: {releaseDate}");
                _leftColumnResults.Add($"");
            }

            if (person.ContainsKey("employer_name") && person["employer_name"] != null && person["employer_name"].ToString() != "")
                _leftColumnResults.Add($"Employer: {person["employer_name"]} ({person["job_title"]})");

            // Display owned vehicles with new fields
            if (person.ContainsKey("owned_vehicles"))
            {
                var vehicles = person["owned_vehicles"] as List<Dictionary<string, object>>;
                if (vehicles != null && vehicles.Count > 0)
                {
                    _leftColumnResults.Add($"Vehicles ({vehicles.Count}):");
                    foreach (var vehicle in vehicles)
                    {
                        string plate = vehicle.ContainsKey("license_plate") ? vehicle["license_plate"].ToString() : "No Plate";
                        string model = vehicle.ContainsKey("vehicle_model") ? vehicle["vehicle_model"].ToString() : "Unknown";
                        string color1 = vehicle.ContainsKey("color_primary") ? vehicle["color_primary"].ToString() : "";
                        string color2 = vehicle.ContainsKey("color_secondary") ? vehicle["color_secondary"].ToString() : "";

                        string colorText = string.IsNullOrEmpty(color1) ? "" : $" ({color1}";
                        colorText += string.IsNullOrEmpty(color2) ? "" : $"/{color2})";

                        _leftColumnResults.Add($"  • {plate}: {model}{colorText}");

                        // Show vehicle status flags
                        if (vehicle.ContainsKey("is_stolen") && Convert.ToInt32(vehicle["is_stolen"]) == 1)
                            _leftColumnResults.Add($"    ~r~[STOLEN]~w~");

                        if (vehicle.ContainsKey("is_impounded") && Convert.ToInt32(vehicle["is_impounded"]) == 1)
                            _leftColumnResults.Add($"    ~r~[IMPOUNDED]~w~");

                        if (vehicle.ContainsKey("no_registration") && Convert.ToInt32(vehicle["no_registration"]) == 1)
                            _leftColumnResults.Add($"    ~r~[NO REGISTRATION]~w~");

                        if (vehicle.ContainsKey("no_insurance") && Convert.ToInt32(vehicle["no_insurance"]) == 1)
                            _leftColumnResults.Add($"    ~r~[NO INSURANCE]~w~");
                    }
                }
            }

            _leftColumnResults.Add($"");
            _leftColumnResults.Add("~g~Press F6 to issue ticket");

            LoadCitationHistory(person);

            _activePersonField = PersonField.FirstName;
        }

        private void OpenTicketMenu()
        {
            if (_lastPersonResults == null || _lastPersonResults.Count == 0)
                return;

            _currentSelectedPerson = _lastPersonResults[_selectedResultIndex];
            _currentScreen = ScreenMode.TicketMenu;
            _selectedTicketIndex = 0;
            _ticketMenuScrollOffset = 0;
            _citationLocation.Clear();
            _showingArrests = false;

            Game.DisplayNotification($"Select ticket for {_currentSelectedPerson["first_name"]} {_currentSelectedPerson["last_name"]}");
        }

        //=============================================================================
        // FIXED: IssueSelectedTicket method - Using vehicle ID 1 as placeholder
        //=============================================================================
        private void IssueSelectedTicket()
        {
            if (_currentSelectedPerson == null)
            {
                Game.DisplayNotification("~r~No person selected");
                _currentScreen = ScreenMode.Search;
                return;
            }

            List<TicketTemplate> currentList = _showingArrests ? _arrestTemplates : _ticketTemplates;
            if (currentList.Count == 0) return;

            var ticket = currentList[_selectedTicketIndex];
            string location = _citationLocation.ToString();

            if (string.IsNullOrEmpty(location))
            {
                location = "Los Santos";
            }

            int pedId = Convert.ToInt32(_currentSelectedPerson["id"]);
            string personName = $"{_currentSelectedPerson["first_name"]} {_currentSelectedPerson["last_name"]}";

            // Use vehicle ID 1 as placeholder for citations without a vehicle
            // Vehicle ID 1 is STARK01 and always exists in the database
            _database.AddTicket(pedId, 1, ticket.Description, ticket.Fine, location);

            if (ticket.IsArrestable)
            {
                // Also mark them as arrested/incarcerated in database
                _database.IncarceratePed(pedId, ticket.Description, ticket.JailDays, location);
                Game.DisplayNotification($"~r~ARREST~w~ issued to {personName} - {ticket.JailDays} days");
                Game.LogTrivial($"[ARREST] {personName} arrested for {ticket.Description}, {ticket.JailDays} days");
            }
            else
            {
                Game.DisplayNotification($"~g~Citation~w~ issued to {personName} - ${ticket.Fine}");
                Game.LogTrivial($"[CITATION] {personName} cited for {ticket.Description}, ${ticket.Fine}");
            }

            _currentScreen = ScreenMode.Search;
            _activePersonField = PersonField.FirstName;
            PerformSearch();
        }

        private void DrawComputerScreen()
        {
            float centerX = 0.5f;

            NativeFunction.Natives.DRAW_RECT(centerX, 0.5f, 0.7f, 0.85f, 0, 0, 0, 220);
            NativeFunction.Natives.DRAW_RECT(centerX, 0.1f, 0.7f, 0.05f, 0, 0, 100, 255);

            string headerText = _currentScreen == ScreenMode.TicketMenu ? "SELECT CHARGE" : "PERSISTENT WORLD MDT";
            DrawText(centerX, 0.09f, headerText, 1.0f, 255, 255, 255, 255, true);

            if (_currentScreen == ScreenMode.TicketMenu)
            {
                DrawTicketMenu(centerX);
            }
            else
            {
                DrawSearchScreen(centerX);
            }
        }

        private void DrawSearchScreen(float centerX)
        {
            float vehicleTabX = centerX - 0.1f;
            float personTabX = centerX + 0.1f;

            int tabR = _currentSearchMode == SearchMode.Vehicle ? 50 : 30;
            int tabG = _currentSearchMode == SearchMode.Vehicle ? 50 : 30;
            int tabB = _currentSearchMode == SearchMode.Vehicle ? 150 : 50;
            NativeFunction.Natives.DRAW_RECT(vehicleTabX, 0.15f, 0.15f, 0.04f, tabR, tabG, tabB, 255);
            DrawText(vehicleTabX, 0.14f, "VEHICLE", 0.6f, 255, 255, 255, 255, true);

            tabR = _currentSearchMode == SearchMode.Person ? 50 : 30;
            tabG = _currentSearchMode == SearchMode.Person ? 50 : 30;
            tabB = _currentSearchMode == SearchMode.Person ? 150 : 50;
            NativeFunction.Natives.DRAW_RECT(personTabX, 0.15f, 0.15f, 0.04f, tabR, tabG, tabB, 255);
            DrawText(personTabX, 0.14f, "PERSON", 0.6f, 255, 255, 255, 255, true);

            float inputStartY = 0.2f;

            if (_currentSearchMode == SearchMode.Vehicle)
            {
                DrawText(centerX - 0.3f, inputStartY, "License:", 0.5f, 200, 200, 200, 255, false);

                string plateDisplay = _vehiclePlateInput.ToString();
                if (_cursorVisible)
                    plateDisplay += "_";
                else
                    plateDisplay += " ";

                DrawText(centerX - 0.1f, inputStartY, plateDisplay, 0.6f, 0, 255, 255, 255, false);
            }
            else
            {
                // First name
                DrawText(centerX - 0.3f, inputStartY, "First:", 0.5f, 200, 200, 200, 255, false);

                string firstNameDisplay = _personFirstNameInput.ToString();
                if (_activePersonField == PersonField.FirstName)
                    firstNameDisplay += _cursorVisible ? "_" : " ";

                DrawText(centerX - 0.1f, inputStartY, firstNameDisplay, 0.6f, 0, 255, 255, 255, false);

                // Last name
                DrawText(centerX - 0.3f, inputStartY + 0.05f, "Last:", 0.5f, 200, 200, 200, 255, false);

                string lastNameDisplay = _personLastNameInput.ToString();
                if (_activePersonField == PersonField.LastName)
                    lastNameDisplay += _cursorVisible ? "_" : " ";

                DrawText(centerX - 0.1f, inputStartY + 0.05f, lastNameDisplay, 0.6f, 0, 255, 255, 255, false);

                string activeField = _activePersonField == PersonField.FirstName ? "First Name (active)" : "Last Name (active)";
                DrawText(centerX - 0.3f, inputStartY + 0.1f, $"Active: {activeField}", 0.4f, 150, 150, 150, 255, false);
            }

            // Draw suggestions
            if (_showSuggestions)
            {
                float suggestionY = inputStartY + (_currentSearchMode == SearchMode.Vehicle ? 0.05f : 0.15f);
                DrawText(centerX - 0.3f, suggestionY, "Suggestions:", 0.4f, 255, 255, 0, 255, false);

                suggestionY += 0.04f;
                for (int i = 0; i < _quickSuggestions.Count && i < 5; i++)
                {
                    string prefix = (i == _selectedSuggestionIndex) ? "→ " : "  ";
                    int r = (i == _selectedSuggestionIndex) ? 0 : 255;
                    int g = (i == _selectedSuggestionIndex) ? 255 : 255;
                    int b = (i == _selectedSuggestionIndex) ? 0 : 255;

                    string suggestion = _quickSuggestions[i];
                    if (suggestion.Length > 30)
                        suggestion = suggestion.Substring(0, 27) + "...";

                    DrawText(centerX - 0.28f, suggestionY, prefix + suggestion, 0.4f, r, g, b, 255, false);
                    suggestionY += 0.03f;
                }
                DrawText(centerX - 0.3f, suggestionY, "A/ENTER to select", 0.3f, 150, 150, 150, 255, false);
            }

            float hintY = _showSuggestions ? 0.45f : 0.35f;
            if ((_currentSearchMode == SearchMode.Vehicle && _vehiclePlateInput.Length > 0) ||
                (_currentSearchMode == SearchMode.Person && (_personFirstNameInput.Length > 0 || _personLastNameInput.Length > 0)))
            {
                DrawText(centerX - 0.2f, hintY, "A/ENTER to search", 0.5f, 0, 255, 0, 255, false);
            }

            float leftColX = centerX - 0.3f;
            float rightColX = centerX + 0.05f;
            float resultsStartY = _showSuggestions ? 0.52f : 0.42f;

            if (_leftColumnResults.Count > 0)
            {
                float yPos = resultsStartY;
                foreach (string line in _leftColumnResults)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        yPos += 0.01f;
                        continue;
                    }

                    int r = 255, g = 255, b = 255;
                    string displayLine = line;

                    if (line.StartsWith("~r~"))
                    {
                        r = 255; g = 0; b = 0;
                        displayLine = line.Substring(3);
                    }
                    else if (line.StartsWith("~g~"))
                    {
                        r = 0; g = 255; b = 0;
                        displayLine = line.Substring(3);
                    }
                    else if (line.StartsWith("~y~"))
                    {
                        r = 255; g = 255; b = 0;
                        displayLine = line.Substring(3);
                    }

                    DrawText(leftColX, yPos, displayLine, 0.4f, r, g, b, 255, false);
                    yPos += 0.03f;

                    if (yPos > 0.8f) break;
                }
            }

            if (_rightColumnResults.Count > 0)
            {
                float yPos = resultsStartY;
                foreach (string line in _rightColumnResults)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        yPos += 0.01f;
                        continue;
                    }

                    int r = 255, g = 255, b = 255;
                    string displayLine = line;

                    if (line.StartsWith("~r~"))
                    {
                        r = 255; g = 0; b = 0;
                        displayLine = line.Substring(3);
                    }
                    else if (line.StartsWith("~g~"))
                    {
                        r = 0; g = 255; b = 0;
                        displayLine = line.Substring(3);
                    }
                    else if (line.StartsWith("~y~"))
                    {
                        r = 255; g = 255; b = 0;
                        displayLine = line.Substring(3);
                    }

                    DrawText(rightColX, yPos, displayLine, 0.4f, r, g, b, 255, false);
                    yPos += 0.03f;

                    if (yPos > 0.8f) break;
                }
            }

            float footerY = 0.88f;
            string modeHint = _showSuggestions ?
                "↑↓/DPad Navigate | A Select" :
                (_currentSearchMode == SearchMode.Person ? "↑↓/DPad Switch Field" : "");

            DrawText(centerX, footerY, $"X/TAB Mode | F6 Ticket | B/ESC Close | {modeHint}", 0.35f, 200, 200, 200, 255, true);
        }

        private void DrawTicketMenu(float centerX)
        {
            float yPos = 0.18f;

            if (_currentSelectedPerson != null)
            {
                DrawText(centerX - 0.3f, yPos, $"Issuing to: {_currentSelectedPerson["first_name"]} {_currentSelectedPerson["last_name"]}", 0.5f, 255, 255, 0, 255, false);
                yPos += 0.05f;
            }

            DrawText(centerX - 0.3f, yPos, "Location:", 0.5f, 200, 200, 200, 255, false);
            string locationDisplay = _citationLocation.ToString();
            if (_cursorVisible)
                locationDisplay += "_";
            else
                locationDisplay += " ";
            DrawText(centerX, yPos, locationDisplay, 0.5f, 0, 255, 255, 255, false);
            yPos += 0.05f;

            string modeText = _showingArrests ? "~r~ARRESTABLE OFFENSES~w~ (X/TAB to switch)" : "~g~CITATIONS~w~ (X/TAB to switch)";
            DrawText(centerX - 0.3f, yPos, modeText, 0.45f, 255, 255, 255, 255, false);
            yPos += 0.04f;

            List<TicketTemplate> currentList = _showingArrests ? _arrestTemplates : _ticketTemplates;

            // Show scroll indicator if needed
            if (_ticketMenuScrollOffset > 0)
            {
                DrawText(centerX - 0.28f, yPos - 0.02f, "   ↑ More ↑", 0.35f, 150, 150, 150, 255, false);
            }

            for (int i = _ticketMenuScrollOffset; i < currentList.Count && i < _ticketMenuScrollOffset + MAX_VISIBLE_TICKETS; i++)
            {
                var ticket = currentList[i];
                string prefix = (i == _selectedTicketIndex) ? "→ " : "  ";
                int r = (i == _selectedTicketIndex) ? 0 : 255;
                int g = (i == _selectedTicketIndex) ? 255 : 255;
                int b = (i == _selectedTicketIndex) ? 0 : 255;

                DrawText(centerX - 0.28f, yPos, prefix + ticket.DisplayText, 0.4f, r, g, b, 255, false);
                yPos += 0.03f;
            }

            if (currentList.Count == 0)
            {
                DrawText(centerX - 0.28f, yPos, "  No offenses configured", 0.4f, 255, 0, 0, 255, false);
            }
            else if (_ticketMenuScrollOffset + MAX_VISIBLE_TICKETS < currentList.Count)
            {
                DrawText(centerX - 0.28f, yPos, "   ↓ More ↓", 0.35f, 150, 150, 150, 255, false);
            }

            float footerY = 0.88f;
            DrawText(centerX, footerY, "↑↓/DPad Navigate | X/TAB Switch | A/ENTER Issue | B Back | Type Location", 0.35f, 200, 200, 200, 255, true);
        }

        private void DrawText(float x, float y, string text, float scale, int r, int g, int b, int a, bool centered)
        {
            NativeFunction.Natives.SET_TEXT_FONT(0);
            NativeFunction.Natives.SET_TEXT_SCALE(scale, scale);
            NativeFunction.Natives.SET_TEXT_COLOUR(r, g, b, a);
            NativeFunction.Natives.SET_TEXT_CENTRE(centered);
            NativeFunction.Natives.SET_TEXT_DROPSHADOW(1, 0, 0, 0, 0);
            NativeFunction.Natives.SET_TEXT_EDGE(1, 0, 0, 0, 0);
            NativeFunction.Natives.BEGIN_TEXT_COMMAND_DISPLAY_TEXT("STRING");
            NativeFunction.Natives.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME(text);
            NativeFunction.Natives.END_TEXT_COMMAND_DISPLAY_TEXT(x, y, 0);
        }

        private void Close()
        {
            _isOpen = false;
            _gameFrozen = false;

            Game.LocalPlayer.Character.IsInvincible = false;
            Game.LocalPlayer.Character.KeepTasks = false;
            Game.LocalPlayer.Character.BlockPermanentEvents = false;

            NativeFunction.Natives.FREEZE_ENTITY_POSITION(Game.LocalPlayer.Character, false);
            NativeFunction.Natives.SET_PLAYER_CONTROL(Game.LocalPlayer, true, 0);
            NativeFunction.Natives.DISPLAY_HUD(true);
            NativeFunction.Natives.DISPLAY_RADAR(true);

            Game.TimeScale = 1.0f;

            Game.LogTrivial("Computer closed");
            Game.DisplayNotification("Computer ~r~closed");
        }
    }
}