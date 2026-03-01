using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using Rage;
using Rage.Native;
using PersistentWorld.Database;
using System.IO;
using System.Linq;
using LSPD_First_Response.Mod.API;
using LSPD_First_Response.Engine.Scripting.Entities;

namespace PersistentWorld.Computer
{
    public class ComputerScreen
    {
        private DatabaseManager _database;
        private bool _isOpen = false;
        private bool _gameFrozen = false;

        // Config settings
        private Config _config;

        // UI state
        private enum ScreenMode { Search, TicketMenu, VehicleSelection, PersonDetails, VehicleDetails }
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
        private Dictionary<string, object> _currentVehicle = null;

        // Ticket menu state
        private List<TicketTemplate> _ticketTemplates = new List<TicketTemplate>();
        private List<TicketTemplate> _arrestTemplates = new List<TicketTemplate>();
        private int _selectedTicketIndex = 0;
        private int _ticketMenuScrollOffset = 0;
        private const int MAX_VISIBLE_TICKETS = 10;
        private Dictionary<string, object> _currentSelectedPerson = null;
        private StringBuilder _citationLocation = new StringBuilder();
        private bool _showingArrests = false;

        // Vehicle selection state
        private List<Dictionary<string, object>> _availableVehicles = new List<Dictionary<string, object>>();
        private Dictionary<string, object> _selectedVehicle = null;
        private StringBuilder _vehicleSearchInput = new StringBuilder();
        private List<string> _vehicleSuggestions = new List<string>();
        private int _selectedVehicleSuggestionIndex = -1;
        private bool _showVehicleSuggestions = false;

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
        private Dictionary<Keys, DateTime> _lastKeyStates = new Dictionary<Keys, DateTime>();
        private Dictionary<int, DateTime> _lastControllerStates = new Dictionary<int, DateTime>();
        private const int KEY_DEBOUNCE_MS = 250;
        private const int CONTROLLER_DEBOUNCE_MS = 300;

        // Config path
        private string _configPath;

        // Person suggestions loaded from database
        private List<PersonSuggestion> _personSuggestions = new List<PersonSuggestion>();

        // Vehicle suggestions - Loaded from database
        private List<VehicleSuggestion> _vehicleSuggestionsList = new List<VehicleSuggestion>();

        // Currently pulled over suspect (from LSPDFR)
        private Ped _currentSuspect = null;
        private Vehicle _currentSuspectVehicle = null;
        private string _currentSuspectName = "";

        // Flag to track if autofill has been done this session
        private bool _hasAutofilled = false;
        private bool _hasAutoFilledPlate = false;

        // Store original audio state
        private string _originalAudioMode = null;

        // Track if we're showing owner info prompt
        private bool _showingOwnerPrompt = false;

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
            public int Id { get; set; }
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

        public ComputerScreen(DatabaseManager database, Config config)
        {
            _database = database;
            _config = config;

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
                            if (ped == null) continue;

                            string firstName = GetSafeString(ped, "first_name");
                            string lastName = GetSafeString(ped, "last_name");
                            string fullName = $"{firstName} {lastName}".Trim();
                            string employer = GetSafeString(ped, "employer_name");
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

        // Helper method to safely get string from dictionary
        private string GetSafeString(Dictionary<string, object> dict, string key)
        {
            if (dict == null) return "";
            if (!dict.ContainsKey(key)) return "";
            if (dict[key] == null) return "";
            if (dict[key] == DBNull.Value) return "";
            return dict[key].ToString();
        }

        // Helper method to safely get int from dictionary
        private int GetSafeInt(Dictionary<string, object> dict, string key, int defaultValue = 0)
        {
            if (dict == null) return defaultValue;
            if (!dict.ContainsKey(key)) return defaultValue;
            if (dict[key] == null) return defaultValue;
            if (dict[key] == DBNull.Value) return defaultValue;

            try
            {
                return Convert.ToInt32(dict[key]);
            }
            catch
            {
                return defaultValue;
            }
        }

        // Helper method to safely get bool from dictionary
        private bool GetSafeBool(Dictionary<string, object> dict, string key, bool defaultValue = false)
        {
            if (dict == null) return defaultValue;
            if (!dict.ContainsKey(key)) return defaultValue;
            if (dict[key] == null) return defaultValue;
            if (dict[key] == DBNull.Value) return defaultValue;

            try
            {
                if (dict[key] is int)
                    return (int)dict[key] == 1;
                return Convert.ToBoolean(dict[key]);
            }
            catch
            {
                return defaultValue;
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
                    _vehicleSuggestionsList.Clear();
                    foreach (var ped in allPeds)
                    {
                        if (ped == null) continue;

                        // Check if person has vehicles
                        if (ped.ContainsKey("owned_vehicles"))
                        {
                            var vehicles = ped["owned_vehicles"] as List<Dictionary<string, object>>;
                            if (vehicles != null)
                            {
                                foreach (var vehicle in vehicles)
                                {
                                    if (vehicle == null) continue;

                                    string plate = GetSafeString(vehicle, "license_plate");
                                    string model = GetSafeString(vehicle, "vehicle_model");
                                    int id = GetSafeInt(vehicle, "id");
                                    string ownerName = $"{GetSafeString(ped, "first_name")} {GetSafeString(ped, "last_name")}".Trim();

                                    if (!string.IsNullOrEmpty(plate) && id > 0)
                                    {
                                        _vehicleSuggestionsList.Add(new VehicleSuggestion
                                        {
                                            Id = id,
                                            LicensePlate = plate.ToUpper().Trim(),
                                            Model = model,
                                            OwnerName = ownerName,
                                            DisplayName = $"{plate.Trim()} - {model} ({ownerName})"
                                        });
                                    }
                                }
                            }
                        }
                    }
                    Game.LogTrivial($"[Computer] Loaded {_vehicleSuggestionsList.Count} vehicle suggestions");
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

        private void GetCurrentPullover()
        {
            try
            {
                // Try to get the current pullover handle from LSPDFR
                LHandle pullover = Functions.GetCurrentPullover();

                if (pullover != null)
                {
                    // Get the suspect from the pullover
                    _currentSuspect = Functions.GetPulloverSuspect(pullover);

                    if (_currentSuspect != null && _currentSuspect.Exists())
                    {
                        // Get suspect's name from Persona
                        var persona = Functions.GetPersonaForPed(_currentSuspect);
                        if (persona != null)
                        {
                            if (!string.IsNullOrEmpty(persona.FullName) && persona.FullName != "John Doe")
                            {
                                _currentSuspectName = persona.FullName;
                            }
                            else
                            {
                                // Build name from parts
                                string firstName = persona.Forename ?? "";
                                string lastName = persona.Surname ?? "";
                                _currentSuspectName = $"{firstName} {lastName}".Trim();

                                if (string.IsNullOrEmpty(_currentSuspectName))
                                    _currentSuspectName = "Unknown";
                            }

                            Game.LogTrivial($"[Computer] Pullover suspect found: {_currentSuspectName}");
                        }

                        // Get the vehicle they were pulled over in
                        _currentSuspectVehicle = _currentSuspect.CurrentVehicle;

                        if (_currentSuspectVehicle != null && _currentSuspectVehicle.Exists())
                        {
                            Game.LogTrivial($"[Computer] Suspect vehicle: {_currentSuspectVehicle.LicensePlate}");
                        }
                    }
                }
                else
                {
                    // No active pullover
                    _currentSuspect = null;
                    _currentSuspectVehicle = null;
                    _currentSuspectName = "";
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[Computer] Error getting pullover info: {ex.Message}");
                _currentSuspect = null;
                _currentSuspectVehicle = null;
                _currentSuspectName = "";
            }
        }

        private void AutoFillPullover()
        {
            // Only auto-fill if we haven't already done so this session
            if (!_hasAutofilled && _currentSuspect != null && _currentSuspect.Exists() && !string.IsNullOrEmpty(_currentSuspectName) && _currentSuspectName != "Unknown")
            {
                // Switch to person mode
                _currentSearchMode = SearchMode.Person;

                // Split the name
                string[] nameParts = _currentSuspectName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (nameParts.Length >= 1)
                {
                    _personFirstNameInput.Clear();
                    _personFirstNameInput.Append(nameParts[0]);

                    if (nameParts.Length > 1)
                    {
                        _personLastNameInput.Clear();
                        _personLastNameInput.Append(string.Join(" ", nameParts.Skip(1)));
                    }

                    Game.LogTrivial($"[Computer] Auto-filled suspect name: {_currentSuspectName}");
                }

                _hasAutofilled = true;
            }

            // Auto-fill vehicle plate with Trim() to remove extra spaces
            if (!_hasAutoFilledPlate && _currentSuspectVehicle != null && _currentSuspectVehicle.Exists())
            {
                string plate = _currentSuspectVehicle.LicensePlate;
                if (!string.IsNullOrEmpty(plate))
                {
                    _vehiclePlateInput.Clear();
                    _vehiclePlateInput.Append(plate.Trim());
                    _hasAutoFilledPlate = true;
                    Game.LogTrivial($"[Computer] Auto-filled vehicle plate: '{plate.Trim()}' (was '{plate}')");
                }
            }

            // Perform search if we have a suspect name and haven't autofilled yet
            if (!_hasAutofilled && _currentSuspect != null && _currentSuspect.Exists() && !string.IsNullOrEmpty(_currentSuspectName) && _currentSuspectName != "Unknown")
            {
                // Small delay to let UI settle
                GameFiber.Wait(100);
                PerformSearch();
            }
        }


        private bool IsPlayerInEmergencyVehicle()
        {
            try
            {
                Ped playerPed = Game.LocalPlayer.Character;
                if (playerPed == null || !playerPed.Exists())
                    return false;

                Vehicle currentVehicle = playerPed.CurrentVehicle;
                if (currentVehicle == null || !currentVehicle.Exists())
                    return false;

                // Use native function to check vehicle class/type
                // Vehicle class 18 is emergency vehicles in GTA V
                int vehicleClass = NativeFunction.Natives.GET_VEHICLE_CLASS<int>(currentVehicle);

                // Class 18 = Emergency vehicles (police, ambulance, firetruck)
                return vehicleClass == 18;
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[Computer] Error checking emergency vehicle: {ex.Message}");
                return false;
            }
        }

        private void FreezeAudio(bool freeze)
        {
            try
            {
                if (freeze)
                {
                    // Store current audio mode (not volume, as that's not directly accessible)
                    // Instead, we'll stop all sounds and set audio flags

                    // Stop all currently playing sounds
                    NativeFunction.Natives.STOP_ALL_SOUNDS();

                    // Disable audio for the duration
                    NativeFunction.Natives.SET_AUDIO_FLAG("AllowPoliceScannerDuringGameplay", false);
                    NativeFunction.Natives.SET_AUDIO_FLAG("DisableFlightMusic", true);
                    NativeFunction.Natives.SET_AUDIO_FLAG("DisableRadioOnMission", true);

                    // Mute the radio if it's playing
                    NativeFunction.Natives.SET_RADIO_TO_STATION_NAME("OFF");

                    Game.LogTrivial("[Computer] Audio frozen (sounds stopped)");
                }
                else
                {
                    // Restore audio settings
                    NativeFunction.Natives.SET_AUDIO_FLAG("AllowPoliceScannerDuringGameplay", true);
                    NativeFunction.Natives.SET_AUDIO_FLAG("DisableFlightMusic", false);
                    NativeFunction.Natives.SET_AUDIO_FLAG("DisableRadioOnMission", false);

                    Game.LogTrivial("[Computer] Audio unfrozen");
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[Computer] Error freezing audio: {ex.Message}");
            }
        }

        // MODIFIED: Show method with emergency vehicle check
        public void Show()
        {
            // Check if player is in an emergency vehicle
            if (!IsPlayerInEmergencyVehicle())
            {
                Game.DisplayNotification("~r~Computer access denied: Must be in an emergency vehicle");
                Game.LogTrivial("[Computer] Attempted to open while not in emergency vehicle - denied");
                return;
            }

            if (_isOpen) return;

            _isOpen = true;
            _hasAutofilled = false;
            _hasAutoFilledPlate = false;

            // Clear key state dictionaries
            _lastKeyStates.Clear();
            _lastControllerStates.Clear();

            // Get current pullover info
            GetCurrentPullover();

            ResetUI();

            // Auto-fill the pullover info if available
            AutoFillPullover();

            Game.LogTrivial("Computer opened");
            Game.DisplayNotification("Computer ~g~opened");

            // Reload suggestions every time computer opens
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

            // Disable all controls
            for (int i = 0; i < 500; i++)
            {
                NativeFunction.Natives.DISABLE_CONTROL_ACTION(0, i, true);
            }

            // Freeze audio
            FreezeAudio(true);

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

                    // Re-disable all controls
                    for (int i = 0; i < 500; i++)
                    {
                        NativeFunction.Natives.DISABLE_CONTROL_ACTION(0, i, true);
                    }

                    // Keep the suspect frozen if there is one
                    if (_currentSuspect != null && _currentSuspect.Exists())
                    {
                        NativeFunction.Natives.FREEZE_ENTITY_POSITION(_currentSuspect, true);
                        _currentSuspect.BlockPermanentEvents = true;
                        _currentSuspect.KeepTasks = true;
                    }

                    // Keep the suspect vehicle frozen
                    if (_currentSuspectVehicle != null && _currentSuspectVehicle.Exists())
                    {
                        NativeFunction.Natives.FREEZE_ENTITY_POSITION(_currentSuspectVehicle, true);
                    }
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
            _currentVehicle = null;
            _selectedTicketIndex = 0;
            _ticketMenuScrollOffset = 0;
            _citationLocation.Clear();
            _showingArrests = false;
            _showingOwnerPrompt = false;

            // Reset vehicle selection
            _availableVehicles.Clear();
            _selectedVehicle = null;
            _vehicleSearchInput.Clear();
            _vehicleSuggestions.Clear();
            _selectedVehicleSuggestionIndex = -1;
            _showVehicleSuggestions = false;
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
                    foreach (var vehicle in _vehicleSuggestionsList)
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
                    foreach (var vehicle in _vehicleSuggestionsList.Take(10))
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

        private void UpdateVehicleSuggestions()
        {
            _vehicleSuggestions.Clear();

            string input = _vehicleSearchInput.ToString().ToUpper();

            // Always show owner's vehicle first if available
            if (_currentSelectedPerson != null && string.IsNullOrEmpty(input))
            {
                string firstName = GetSafeString(_currentSelectedPerson, "first_name");
                string lastName = GetSafeString(_currentSelectedPerson, "last_name");
                string ownerName = $"{firstName} {lastName}".Trim();

                // Find vehicles owned by this person
                var ownerVehicles = _vehicleSuggestionsList.Where(v => v.OwnerName.Equals(ownerName, StringComparison.OrdinalIgnoreCase)).ToList();

                foreach (var vehicle in ownerVehicles)
                {
                    _vehicleSuggestions.Add($"→ {vehicle.DisplayName} (OWNER)");
                }
            }

            // Add matching vehicles based on search input
            if (input.Length >= 1)
            {
                foreach (var vehicle in _vehicleSuggestionsList)
                {
                    if (vehicle.LicensePlate.Contains(input) ||
                        vehicle.Model.ToUpper().Contains(input) ||
                        vehicle.OwnerName.ToUpper().Contains(input))
                    {
                        string displayName = vehicle.DisplayName;
                        if (!_vehicleSuggestions.Contains(displayName) && !_vehicleSuggestions.Contains($"→ {displayName} (OWNER)"))
                        {
                            _vehicleSuggestions.Add(displayName);
                        }
                    }
                }
            }
            else if (string.IsNullOrEmpty(input) && _vehicleSuggestions.Count == 0)
            {
                // Show some recent vehicles when no input and no owner vehicles
                foreach (var vehicle in _vehicleSuggestionsList.Take(10))
                {
                    _vehicleSuggestions.Add(vehicle.DisplayName);
                }
            }

            // Remove duplicates and limit
            _vehicleSuggestions = _vehicleSuggestions.Distinct().Take(10).ToList();

            // Show suggestions if we have any
            _showVehicleSuggestions = _vehicleSuggestions.Count > 0;

            if (_showVehicleSuggestions)
            {
                if (_selectedVehicleSuggestionIndex >= _vehicleSuggestions.Count)
                    _selectedVehicleSuggestionIndex = _vehicleSuggestions.Count - 1;
                if (_selectedVehicleSuggestionIndex < 0)
                    _selectedVehicleSuggestionIndex = 0;
            }
            else
            {
                _selectedVehicleSuggestionIndex = -1;
            }
        }

        private void HandleInput()
        {
            // Check if enough time has passed since last ANY key press
            if ((DateTime.Now - _lastKeyPress).TotalMilliseconds < KEY_DEBOUNCE_MS)
                return;

            bool keyProcessed = false;

            // ONLY ESCAPE closes the computer - with debounce
            if (Game.IsKeyDown(Keys.Escape))
            {
                // Check individual key debounce
                if (!_lastKeyStates.ContainsKey(Keys.Escape) ||
                    (DateTime.Now - _lastKeyStates[Keys.Escape]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Escape] = DateTime.Now;
                    _lastKeyPress = DateTime.Now;

                    if (_currentScreen == ScreenMode.TicketMenu)
                    {
                        _currentScreen = ScreenMode.Search;
                    }
                    else if (_currentScreen == ScreenMode.VehicleSelection)
                    {
                        _currentScreen = ScreenMode.TicketMenu;
                    }
                    else if (_currentScreen == ScreenMode.PersonDetails)
                    {
                        _currentScreen = ScreenMode.Search;
                    }
                    else if (_currentScreen == ScreenMode.VehicleDetails)
                    {
                        _currentScreen = ScreenMode.Search;
                        _showingOwnerPrompt = false;
                    }
                    else
                    {
                        Close();
                    }
                }
                return;
            }

            if (_currentScreen == ScreenMode.Search)
            {
                HandleSearchInput(ref keyProcessed);
            }
            else if (_currentScreen == ScreenMode.TicketMenu)
            {
                HandleTicketMenuInput(ref keyProcessed);
            }
            else if (_currentScreen == ScreenMode.VehicleSelection)
            {
                HandleVehicleSelectionInput(ref keyProcessed);
            }
            else if (_currentScreen == ScreenMode.PersonDetails)
            {
                HandlePersonDetailsInput(ref keyProcessed);
            }
            else if (_currentScreen == ScreenMode.VehicleDetails)
            {
                HandleVehicleDetailsInput(ref keyProcessed);
            }

            if (keyProcessed)
            {
                _lastKeyPress = DateTime.Now;
            }
        }

        private void HandlePersonDetailsInput(ref bool keyProcessed)
        {
            // F6 to issue ticket (keyboard)
            if (Game.IsKeyDown(Keys.F6))
            {
                if (!_lastKeyStates.ContainsKey(Keys.F6) ||
                    (DateTime.Now - _lastKeyStates[Keys.F6]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.F6] = DateTime.Now;
                    OpenTicketMenu();
                    keyProcessed = true;
                }
                return;
            }

            // Back to search
            if (Game.IsKeyDown(Keys.Back))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Back) ||
                    (DateTime.Now - _lastKeyStates[Keys.Back]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Back] = DateTime.Now;
                    _currentScreen = ScreenMode.Search;
                    keyProcessed = true;
                }
                return;
            }
        }

        private void HandleVehicleDetailsInput(ref bool keyProcessed)
        {
            // F6 to issue ticket (keyboard)
            if (Game.IsKeyDown(Keys.F6))
            {
                if (!_lastKeyStates.ContainsKey(Keys.F6) ||
                    (DateTime.Now - _lastKeyStates[Keys.F6]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.F6] = DateTime.Now;

                    if (_currentVehicle != null && _currentVehicle.ContainsKey("ped_id") && _currentVehicle["ped_id"] != null)
                    {
                        int pedId = GetSafeInt(_currentVehicle, "ped_id");
                        string firstName = GetSafeString(_currentVehicle, "first_name");
                        string lastName = GetSafeString(_currentVehicle, "last_name");

                        if (!string.IsNullOrEmpty(firstName) && !string.IsNullOrEmpty(lastName))
                        {
                            var personResults = _database.LookupByName(firstName, lastName);
                            if (personResults != null && personResults.Count > 0)
                            {
                                _lastPersonResults = personResults;
                                _selectedResultIndex = 0;
                                _currentSelectedPerson = personResults[0];
                                OpenTicketMenu();
                            }
                        }
                    }
                    keyProcessed = true;
                }
                return;
            }

            // Enter now requires TWO presses to go to owner (first shows prompt, second goes)
            if (Game.IsKeyDown(Keys.Enter))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Enter) ||
                    (DateTime.Now - _lastKeyStates[Keys.Enter]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Enter] = DateTime.Now;

                    if (!_showingOwnerPrompt)
                    {
                        // First press - show prompt
                        _showingOwnerPrompt = true;
                        Game.DisplayNotification("Press ~g~ENTER~w~ again to view owner details");
                    }
                    else
                    {
                        // Second press - go to owner
                        if (_currentVehicle != null && _currentVehicle.ContainsKey("ped_id") && _currentVehicle["ped_id"] != null)
                        {
                            int pedId = GetSafeInt(_currentVehicle, "ped_id");
                            string firstName = GetSafeString(_currentVehicle, "first_name");
                            string lastName = GetSafeString(_currentVehicle, "last_name");

                            if (!string.IsNullOrEmpty(firstName) && !string.IsNullOrEmpty(lastName))
                            {
                                _currentSearchMode = SearchMode.Person;
                                _personFirstNameInput.Clear();
                                _personFirstNameInput.Append(firstName);
                                _personLastNameInput.Clear();
                                _personLastNameInput.Append(lastName);
                                PerformSearch();
                                _showingOwnerPrompt = false;
                            }
                        }
                    }
                    keyProcessed = true;
                }
                return;
            }

            // Back to search
            if (Game.IsKeyDown(Keys.Back))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Back) ||
                    (DateTime.Now - _lastKeyStates[Keys.Back]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Back] = DateTime.Now;
                    _currentScreen = ScreenMode.Search;
                    _showingOwnerPrompt = false;
                    keyProcessed = true;
                }
                return;
            }
        }

        private void HandleControllerInput()
        {
            if ((DateTime.Now - _lastControllerInput).TotalMilliseconds < CONTROLLER_DEBOUNCE_MS)
                return;

            bool dpadDown = NativeFunction.Natives.IS_DISABLED_CONTROL_PRESSED<int>(0, 187) == 1;
            bool dpadUp = NativeFunction.Natives.IS_DISABLED_CONTROL_PRESSED<int>(0, 188) == 1;
            bool dpadLeft = NativeFunction.Natives.IS_DISABLED_CONTROL_PRESSED<int>(0, 189) == 1;
            bool dpadRight = NativeFunction.Natives.IS_DISABLED_CONTROL_PRESSED<int>(0, 190) == 1;
            bool aButton = NativeFunction.Natives.IS_DISABLED_CONTROL_PRESSED<int>(0, 201) == 1;
            bool bButton = NativeFunction.Natives.IS_DISABLED_CONTROL_PRESSED<int>(0, 202) == 1;
            bool xButton = NativeFunction.Natives.IS_DISABLED_CONTROL_PRESSED<int>(0, 203) == 1;
            bool yButton = NativeFunction.Natives.IS_DISABLED_CONTROL_PRESSED<int>(0, 204) == 1;
            bool leftBumper = NativeFunction.Natives.IS_DISABLED_CONTROL_PRESSED<int>(0, 205) == 1;
            bool rightBumper = NativeFunction.Natives.IS_DISABLED_CONTROL_PRESSED<int>(0, 206) == 1;

            // Dictionary keys for controller buttons
            int B_BUTTON = 202;
            int RIGHT_BUMPER = 206;
            int X_BUTTON = 203;
            int Y_BUTTON = 204;
            int A_BUTTON = 201;
            int DPAD_UP = 188;
            int DPAD_DOWN = 187;
            int DPAD_LEFT = 189;
            int DPAD_RIGHT = 190;

            // B button = Back/Cancel
            if (bButton)
            {
                if (!_lastControllerStates.ContainsKey(B_BUTTON) ||
                    (DateTime.Now - _lastControllerStates[B_BUTTON]).TotalMilliseconds > CONTROLLER_DEBOUNCE_MS)
                {
                    _lastControllerStates[B_BUTTON] = DateTime.Now;

                    if (_currentScreen == ScreenMode.TicketMenu)
                    {
                        if (_citationLocation.Length > 0)
                        {
                            _citationLocation.Remove(_citationLocation.Length - 1, 1);
                        }
                        else
                        {
                            _currentScreen = ScreenMode.Search;
                        }
                    }
                    else if (_currentScreen == ScreenMode.VehicleSelection)
                    {
                        if (_vehicleSearchInput.Length > 0)
                        {
                            _vehicleSearchInput.Remove(_vehicleSearchInput.Length - 1, 1);
                            UpdateVehicleSuggestions();
                        }
                        else
                        {
                            _currentScreen = ScreenMode.TicketMenu;
                        }
                    }
                    else if (_currentScreen == ScreenMode.PersonDetails)
                    {
                        _currentScreen = ScreenMode.Search;
                    }
                    else if (_currentScreen == ScreenMode.VehicleDetails)
                    {
                        _currentScreen = ScreenMode.Search;
                        _showingOwnerPrompt = false;
                    }
                    else if (_currentScreen != ScreenMode.Search)
                    {
                        _currentScreen = ScreenMode.Search;
                    }
                    _lastControllerInput = DateTime.Now;
                }
                return;
            }

            // Right Bumper = Switch Mode / Switch between citations and arrests
            if (rightBumper)
            {
                if (!_lastControllerStates.ContainsKey(RIGHT_BUMPER) ||
                    (DateTime.Now - _lastControllerStates[RIGHT_BUMPER]).TotalMilliseconds > CONTROLLER_DEBOUNCE_MS)
                {
                    _lastControllerStates[RIGHT_BUMPER] = DateTime.Now;

                    if (_currentScreen == ScreenMode.Search)
                    {
                        SwitchMode();
                    }
                    else if (_currentScreen == ScreenMode.TicketMenu)
                    {
                        _showingArrests = !_showingArrests;
                        _selectedTicketIndex = 0;
                        _ticketMenuScrollOffset = 0;
                    }
                    _lastControllerInput = DateTime.Now;
                }
                return;
            }

            // X button = View Owner (with extra step for vehicle details)
            if (xButton)
            {
                if (!_lastControllerStates.ContainsKey(X_BUTTON) ||
                    (DateTime.Now - _lastControllerStates[X_BUTTON]).TotalMilliseconds > CONTROLLER_DEBOUNCE_MS)
                {
                    _lastControllerStates[X_BUTTON] = DateTime.Now;

                    if (_currentScreen == ScreenMode.VehicleDetails && _currentVehicle != null)
                    {
                        if (!_showingOwnerPrompt)
                        {
                            // First press - show prompt
                            _showingOwnerPrompt = true;
                            Game.DisplayNotification("Press ~b~X~w~ again to view owner details");
                        }
                        else
                        {
                            // Second press - go to owner
                            if (_currentVehicle.ContainsKey("ped_id") && _currentVehicle["ped_id"] != null)
                            {
                                int pedId = GetSafeInt(_currentVehicle, "ped_id");
                                string firstName = GetSafeString(_currentVehicle, "first_name");
                                string lastName = GetSafeString(_currentVehicle, "last_name");

                                if (!string.IsNullOrEmpty(firstName) && !string.IsNullOrEmpty(lastName))
                                {
                                    _currentSearchMode = SearchMode.Person;
                                    _personFirstNameInput.Clear();
                                    _personFirstNameInput.Append(firstName);
                                    _personLastNameInput.Clear();
                                    _personLastNameInput.Append(lastName);
                                    PerformSearch();
                                    _showingOwnerPrompt = false;
                                }
                            }
                        }
                    }
                    _lastControllerInput = DateTime.Now;
                }
                return;
            }

            // Y button = Issue Ticket
            if (yButton)
            {
                if (!_lastControllerStates.ContainsKey(Y_BUTTON) ||
                    (DateTime.Now - _lastControllerStates[Y_BUTTON]).TotalMilliseconds > CONTROLLER_DEBOUNCE_MS)
                {
                    _lastControllerStates[Y_BUTTON] = DateTime.Now;

                    if (_currentScreen == ScreenMode.PersonDetails && _currentSelectedPerson != null)
                    {
                        OpenTicketMenu();
                    }
                    else if (_currentScreen == ScreenMode.VehicleDetails && _currentVehicle != null)
                    {
                        if (_currentVehicle.ContainsKey("ped_id") && _currentVehicle["ped_id"] != null)
                        {
                            int pedId = GetSafeInt(_currentVehicle, "ped_id");
                            string firstName = GetSafeString(_currentVehicle, "first_name");
                            string lastName = GetSafeString(_currentVehicle, "last_name");

                            if (!string.IsNullOrEmpty(firstName) && !string.IsNullOrEmpty(lastName))
                            {
                                var personResults = _database.LookupByName(firstName, lastName);
                                if (personResults != null && personResults.Count > 0)
                                {
                                    _lastPersonResults = personResults;
                                    _selectedResultIndex = 0;
                                    _currentSelectedPerson = personResults[0];
                                    OpenTicketMenu();
                                }
                            }
                        }
                    }
                    _lastControllerInput = DateTime.Now;
                }
                return;
            }

            // A button = Select / Search / Next
            if (aButton)
            {
                if (!_lastControllerStates.ContainsKey(A_BUTTON) ||
                    (DateTime.Now - _lastControllerStates[A_BUTTON]).TotalMilliseconds > CONTROLLER_DEBOUNCE_MS)
                {
                    _lastControllerStates[A_BUTTON] = DateTime.Now;

                    if (_currentScreen == ScreenMode.Search)
                    {
                        if (_showSuggestions && _selectedSuggestionIndex >= 0)
                        {
                            SelectSuggestion();
                        }
                        else
                        {
                            PerformSearch();
                        }
                    }
                    else if (_currentScreen == ScreenMode.PersonDetails && _currentSelectedPerson != null)
                    {
                        OpenTicketMenu();
                    }
                    else if (_currentScreen == ScreenMode.VehicleDetails && _currentVehicle != null)
                    {
                        // For vehicle details, A button now requires two presses
                        if (!_showingOwnerPrompt)
                        {
                            _showingOwnerPrompt = true;
                            Game.DisplayNotification("Press ~g~A~w~ again to view owner details");
                        }
                        else
                        {
                            if (_currentVehicle.ContainsKey("ped_id") && _currentVehicle["ped_id"] != null)
                            {
                                int pedId = GetSafeInt(_currentVehicle, "ped_id");
                                string firstName = GetSafeString(_currentVehicle, "first_name");
                                string lastName = GetSafeString(_currentVehicle, "last_name");

                                if (!string.IsNullOrEmpty(firstName) && !string.IsNullOrEmpty(lastName))
                                {
                                    _currentSearchMode = SearchMode.Person;
                                    _personFirstNameInput.Clear();
                                    _personFirstNameInput.Append(firstName);
                                    _personLastNameInput.Clear();
                                    _personLastNameInput.Append(lastName);
                                    PerformSearch();
                                    _showingOwnerPrompt = false;
                                }
                            }
                        }
                    }
                    else if (_currentScreen == ScreenMode.TicketMenu)
                    {
                        OpenVehicleSelection();
                    }
                    else if (_currentScreen == ScreenMode.VehicleSelection)
                    {
                        if (_showVehicleSuggestions && _selectedVehicleSuggestionIndex >= 0)
                        {
                            SelectVehicleSuggestion();
                        }
                    }
                    _lastControllerInput = DateTime.Now;
                }
                return;
            }

            // D-Pad navigation with debouncing
            if (dpadUp)
            {
                if (!_lastControllerStates.ContainsKey(DPAD_UP) ||
                    (DateTime.Now - _lastControllerStates[DPAD_UP]).TotalMilliseconds > CONTROLLER_DEBOUNCE_MS)
                {
                    _lastControllerStates[DPAD_UP] = DateTime.Now;
                    HandleDPadUp();
                    _lastControllerInput = DateTime.Now;
                }
                return;
            }

            if (dpadDown)
            {
                if (!_lastControllerStates.ContainsKey(DPAD_DOWN) ||
                    (DateTime.Now - _lastControllerStates[DPAD_DOWN]).TotalMilliseconds > CONTROLLER_DEBOUNCE_MS)
                {
                    _lastControllerStates[DPAD_DOWN] = DateTime.Now;
                    HandleDPadDown();
                    _lastControllerInput = DateTime.Now;
                }
                return;
            }

            if (dpadLeft || dpadRight)
            {
                if (_currentScreen == ScreenMode.Search && _currentSearchMode == SearchMode.Person)
                {
                    int dpadKey = dpadLeft ? DPAD_LEFT : DPAD_RIGHT;
                    if (!_lastControllerStates.ContainsKey(dpadKey) ||
                        (DateTime.Now - _lastControllerStates[dpadKey]).TotalMilliseconds > CONTROLLER_DEBOUNCE_MS)
                    {
                        _lastControllerStates[dpadKey] = DateTime.Now;
                        _activePersonField = (_activePersonField == PersonField.FirstName) ?
                            PersonField.LastName : PersonField.FirstName;
                        _lastControllerInput = DateTime.Now;
                    }
                }
                return;
            }
        }

        private void HandleDPadUp()
        {
            if (_currentScreen == ScreenMode.Search)
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
            }
            else if (_currentScreen == ScreenMode.TicketMenu)
            {
                _selectedTicketIndex--;
                List<TicketTemplate> currentList = _showingArrests ? _arrestTemplates : _ticketTemplates;

                if (_selectedTicketIndex < 0)
                    _selectedTicketIndex = currentList.Count - 1;

                if (_selectedTicketIndex < _ticketMenuScrollOffset)
                    _ticketMenuScrollOffset = _selectedTicketIndex;
            }
            else if (_currentScreen == ScreenMode.VehicleSelection)
            {
                if (_showVehicleSuggestions)
                {
                    _selectedVehicleSuggestionIndex--;
                    if (_selectedVehicleSuggestionIndex < 0)
                        _selectedVehicleSuggestionIndex = _vehicleSuggestions.Count - 1;
                }
            }
        }

        private void HandleDPadDown()
        {
            if (_currentScreen == ScreenMode.Search)
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
            }
            else if (_currentScreen == ScreenMode.TicketMenu)
            {
                _selectedTicketIndex++;
                List<TicketTemplate> currentList = _showingArrests ? _arrestTemplates : _ticketTemplates;

                if (_selectedTicketIndex >= currentList.Count)
                    _selectedTicketIndex = 0;

                if (_selectedTicketIndex >= _ticketMenuScrollOffset + MAX_VISIBLE_TICKETS)
                    _ticketMenuScrollOffset = _selectedTicketIndex - MAX_VISIBLE_TICKETS + 1;
            }
            else if (_currentScreen == ScreenMode.VehicleSelection)
            {
                if (_showVehicleSuggestions)
                {
                    _selectedVehicleSuggestionIndex++;
                    if (_selectedVehicleSuggestionIndex >= _vehicleSuggestions.Count)
                        _selectedVehicleSuggestionIndex = 0;
                }
            }
        }

        private void HandleSearchInput(ref bool keyProcessed)
        {
            // Tab key for mode switching (keyboard) - NOT E
            if (Game.IsKeyDown(Keys.Tab))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Tab) ||
                    (DateTime.Now - _lastKeyStates[Keys.Tab]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Tab] = DateTime.Now;
                    SwitchMode();
                    keyProcessed = true;
                }
                return;
            }

            // F6 for ticket menu
            if (Game.IsKeyDown(Keys.F6) && _lastPersonResults != null && _lastPersonResults.Count > 0)
            {
                if (!_lastKeyStates.ContainsKey(Keys.F6) ||
                    (DateTime.Now - _lastKeyStates[Keys.F6]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.F6] = DateTime.Now;
                    OpenTicketMenu();
                    keyProcessed = true;
                }
                return;
            }

            // Enter for search/select
            if (Game.IsKeyDown(Keys.Enter))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Enter) ||
                    (DateTime.Now - _lastKeyStates[Keys.Enter]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Enter] = DateTime.Now;

                    if (_showSuggestions && _selectedSuggestionIndex >= 0)
                    {
                        SelectSuggestion();
                    }
                    else
                    {
                        PerformSearch();
                    }

                    if (_currentSearchMode == SearchMode.Person)
                    {
                        _activePersonField = PersonField.FirstName;
                    }
                    keyProcessed = true;
                }
                return;
            }

            // Field switching for Person mode (Up/Down arrows - NOT E)
            if (_currentSearchMode == SearchMode.Person && (Game.IsKeyDown(Keys.Up) || Game.IsKeyDown(Keys.Down)))
            {
                Keys key = Game.IsKeyDown(Keys.Up) ? Keys.Up : Keys.Down;
                if (!_lastKeyStates.ContainsKey(key) ||
                    (DateTime.Now - _lastKeyStates[key]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[key] = DateTime.Now;
                    _activePersonField = (_activePersonField == PersonField.FirstName) ?
                        PersonField.LastName : PersonField.FirstName;
                    keyProcessed = true;
                }
                return;
            }

            // Suggestion navigation (Up/Down arrows)
            if (_showSuggestions)
            {
                if (Game.IsKeyDown(Keys.Up))
                {
                    if (!_lastKeyStates.ContainsKey(Keys.Up) ||
                        (DateTime.Now - _lastKeyStates[Keys.Up]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                    {
                        _lastKeyStates[Keys.Up] = DateTime.Now;
                        _selectedSuggestionIndex--;
                        if (_selectedSuggestionIndex < 0)
                            _selectedSuggestionIndex = _quickSuggestions.Count - 1;
                        keyProcessed = true;
                    }
                    return;
                }
                if (Game.IsKeyDown(Keys.Down))
                {
                    if (!_lastKeyStates.ContainsKey(Keys.Down) ||
                        (DateTime.Now - _lastKeyStates[Keys.Down]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                    {
                        _lastKeyStates[Keys.Down] = DateTime.Now;
                        _selectedSuggestionIndex++;
                        if (_selectedSuggestionIndex >= _quickSuggestions.Count)
                            _selectedSuggestionIndex = 0;
                        keyProcessed = true;
                    }
                    return;
                }
            }
            else if (_lastPersonResults != null && _lastPersonResults.Count > 1)
            {
                if (Game.IsKeyDown(Keys.Up))
                {
                    if (!_lastKeyStates.ContainsKey(Keys.Up) ||
                        (DateTime.Now - _lastKeyStates[Keys.Up]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                    {
                        _lastKeyStates[Keys.Up] = DateTime.Now;
                        _selectedResultIndex--;
                        if (_selectedResultIndex < 0)
                            _selectedResultIndex = _lastPersonResults.Count - 1;
                        DisplaySelectedPerson();
                        keyProcessed = true;
                    }
                    return;
                }
                if (Game.IsKeyDown(Keys.Down))
                {
                    if (!_lastKeyStates.ContainsKey(Keys.Down) ||
                        (DateTime.Now - _lastKeyStates[Keys.Down]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                    {
                        _lastKeyStates[Keys.Down] = DateTime.Now;
                        _selectedResultIndex++;
                        if (_selectedResultIndex >= _lastPersonResults.Count)
                            _selectedResultIndex = 0;
                        DisplaySelectedPerson();
                        keyProcessed = true;
                    }
                    return;
                }
            }

            // BACKSPACE now ONLY deletes characters
            if (Game.IsKeyDown(Keys.Back))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Back) ||
                    (DateTime.Now - _lastKeyStates[Keys.Back]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Back] = DateTime.Now;
                    HandleBackspace();
                    keyProcessed = true;
                }
                return;
            }

            // Letters A-Z (including E - now ONLY types the letter E)
            for (Keys key = Keys.A; key <= Keys.Z; key++)
            {
                if (Game.IsKeyDown(key))
                {
                    // Check if this specific key was recently pressed
                    if (!_lastKeyStates.ContainsKey(key) ||
                        (DateTime.Now - _lastKeyStates[key]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                    {
                        _lastKeyStates[key] = DateTime.Now;
                        char c = (char)('A' + (key - Keys.A));
                        HandleCharacter(c);
                        keyProcessed = true;
                    }
                    return;
                }
            }

            // Numbers for vehicle mode
            if (_currentSearchMode == SearchMode.Vehicle)
            {
                for (Keys key = Keys.D0; key <= Keys.D9; key++)
                {
                    if (Game.IsKeyDown(key))
                    {
                        if (!_lastKeyStates.ContainsKey(key) ||
                            (DateTime.Now - _lastKeyStates[key]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                        {
                            _lastKeyStates[key] = DateTime.Now;
                            char c = (char)('0' + (key - Keys.D0));
                            HandleCharacter(c);
                            keyProcessed = true;
                        }
                        return;
                    }
                }
            }

            // Space for person mode
            if (_currentSearchMode == SearchMode.Person && Game.IsKeyDown(Keys.Space))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Space) ||
                    (DateTime.Now - _lastKeyStates[Keys.Space]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Space] = DateTime.Now;
                    HandleCharacter(' ');
                    keyProcessed = true;
                }
                return;
            }
        }

        private void HandleTicketMenuInput(ref bool keyProcessed)
        {
            if (Game.IsKeyDown(Keys.Tab))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Tab) ||
                    (DateTime.Now - _lastKeyStates[Keys.Tab]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Tab] = DateTime.Now;
                    _showingArrests = !_showingArrests;
                    _selectedTicketIndex = 0;
                    _ticketMenuScrollOffset = 0;
                    keyProcessed = true;
                }
                return;
            }

            List<TicketTemplate> currentList = _showingArrests ? _arrestTemplates : _ticketTemplates;
            if (currentList.Count == 0) return;

            if (Game.IsKeyDown(Keys.Up))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Up) ||
                    (DateTime.Now - _lastKeyStates[Keys.Up]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Up] = DateTime.Now;
                    _selectedTicketIndex--;
                    if (_selectedTicketIndex < 0)
                        _selectedTicketIndex = currentList.Count - 1;

                    if (_selectedTicketIndex < _ticketMenuScrollOffset)
                        _ticketMenuScrollOffset = _selectedTicketIndex;

                    keyProcessed = true;
                }
                return;
            }

            if (Game.IsKeyDown(Keys.Down))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Down) ||
                    (DateTime.Now - _lastKeyStates[Keys.Down]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Down] = DateTime.Now;
                    _selectedTicketIndex++;
                    if (_selectedTicketIndex >= currentList.Count)
                        _selectedTicketIndex = 0;

                    if (_selectedTicketIndex >= _ticketMenuScrollOffset + MAX_VISIBLE_TICKETS)
                        _ticketMenuScrollOffset = _selectedTicketIndex - MAX_VISIBLE_TICKETS + 1;

                    keyProcessed = true;
                }
                return;
            }

            if (Game.IsKeyDown(Keys.Enter))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Enter) ||
                    (DateTime.Now - _lastKeyStates[Keys.Enter]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Enter] = DateTime.Now;
                    // Move to vehicle selection instead of issuing directly
                    OpenVehicleSelection();
                    keyProcessed = true;
                }
                return;
            }

            if (Game.IsKeyDown(Keys.Back))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Back) ||
                    (DateTime.Now - _lastKeyStates[Keys.Back]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Back] = DateTime.Now;

                    if (_citationLocation.Length > 0)
                    {
                        _citationLocation.Remove(_citationLocation.Length - 1, 1);
                    }
                    else
                    {
                        _currentScreen = ScreenMode.Search;
                    }
                    keyProcessed = true;
                }
                return;
            }

            for (Keys key = Keys.A; key <= Keys.Z; key++)
            {
                if (Game.IsKeyDown(key))
                {
                    if (!_lastKeyStates.ContainsKey(key) ||
                        (DateTime.Now - _lastKeyStates[key]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                    {
                        _lastKeyStates[key] = DateTime.Now;
                        char c = (char)('A' + (key - Keys.A));
                        if (_citationLocation.Length < 30)
                            _citationLocation.Append(c);
                        keyProcessed = true;
                    }
                    return;
                }
            }

            for (Keys key = Keys.D0; key <= Keys.D9; key++)
            {
                if (Game.IsKeyDown(key))
                {
                    if (!_lastKeyStates.ContainsKey(key) ||
                        (DateTime.Now - _lastKeyStates[key]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                    {
                        _lastKeyStates[key] = DateTime.Now;
                        char c = (char)('0' + (key - Keys.D0));
                        if (_citationLocation.Length < 30)
                            _citationLocation.Append(c);
                        keyProcessed = true;
                    }
                    return;
                }
            }

            if (Game.IsKeyDown(Keys.Space))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Space) ||
                    (DateTime.Now - _lastKeyStates[Keys.Space]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Space] = DateTime.Now;
                    if (_citationLocation.Length < 30)
                        _citationLocation.Append(' ');
                    keyProcessed = true;
                }
                return;
            }
        }

        private void HandleVehicleSelectionInput(ref bool keyProcessed)
        {
            if (Game.IsKeyDown(Keys.Enter))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Enter) ||
                    (DateTime.Now - _lastKeyStates[Keys.Enter]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Enter] = DateTime.Now;

                    if (_showVehicleSuggestions && _selectedVehicleSuggestionIndex >= 0)
                    {
                        SelectVehicleSuggestion();
                    }
                    keyProcessed = true;
                }
                return;
            }

            if (Game.IsKeyDown(Keys.Back))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Back) ||
                    (DateTime.Now - _lastKeyStates[Keys.Back]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Back] = DateTime.Now;

                    if (_vehicleSearchInput.Length > 0)
                    {
                        _vehicleSearchInput.Remove(_vehicleSearchInput.Length - 1, 1);
                        UpdateVehicleSuggestions();
                    }
                    else
                    {
                        _currentScreen = ScreenMode.TicketMenu;
                    }
                    keyProcessed = true;
                }
                return;
            }

            if (_showVehicleSuggestions)
            {
                if (Game.IsKeyDown(Keys.Up))
                {
                    if (!_lastKeyStates.ContainsKey(Keys.Up) ||
                        (DateTime.Now - _lastKeyStates[Keys.Up]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                    {
                        _lastKeyStates[Keys.Up] = DateTime.Now;
                        _selectedVehicleSuggestionIndex--;
                        if (_selectedVehicleSuggestionIndex < 0)
                            _selectedVehicleSuggestionIndex = _vehicleSuggestions.Count - 1;
                        keyProcessed = true;
                    }
                    return;
                }
                if (Game.IsKeyDown(Keys.Down))
                {
                    if (!_lastKeyStates.ContainsKey(Keys.Down) ||
                        (DateTime.Now - _lastKeyStates[Keys.Down]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                    {
                        _lastKeyStates[Keys.Down] = DateTime.Now;
                        _selectedVehicleSuggestionIndex++;
                        if (_selectedVehicleSuggestionIndex >= _vehicleSuggestions.Count)
                            _selectedVehicleSuggestionIndex = 0;
                        keyProcessed = true;
                    }
                    return;
                }
            }

            // Letters A-Z
            for (Keys key = Keys.A; key <= Keys.Z; key++)
            {
                if (Game.IsKeyDown(key))
                {
                    if (!_lastKeyStates.ContainsKey(key) ||
                        (DateTime.Now - _lastKeyStates[key]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                    {
                        _lastKeyStates[key] = DateTime.Now;
                        char c = (char)('A' + (key - Keys.A));
                        if (_vehicleSearchInput.Length < 20)
                            _vehicleSearchInput.Append(char.ToUpper(c));
                        UpdateVehicleSuggestions();
                        keyProcessed = true;
                    }
                    return;
                }
            }

            // Numbers
            for (Keys key = Keys.D0; key <= Keys.D9; key++)
            {
                if (Game.IsKeyDown(key))
                {
                    if (!_lastKeyStates.ContainsKey(key) ||
                        (DateTime.Now - _lastKeyStates[key]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                    {
                        _lastKeyStates[key] = DateTime.Now;
                        char c = (char)('0' + (key - Keys.D0));
                        if (_vehicleSearchInput.Length < 20)
                            _vehicleSearchInput.Append(c);
                        UpdateVehicleSuggestions();
                        keyProcessed = true;
                    }
                    return;
                }
            }

            if (Game.IsKeyDown(Keys.Space))
            {
                if (!_lastKeyStates.ContainsKey(Keys.Space) ||
                    (DateTime.Now - _lastKeyStates[Keys.Space]).TotalMilliseconds > KEY_DEBOUNCE_MS)
                {
                    _lastKeyStates[Keys.Space] = DateTime.Now;
                    if (_vehicleSearchInput.Length < 20)
                        _vehicleSearchInput.Append(' ');
                    UpdateVehicleSuggestions();
                    keyProcessed = true;
                }
                return;
            }
        }

        private void HandleCharacter(char c)
        {
            if (_currentScreen == ScreenMode.Search)
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
            else if (_currentScreen == ScreenMode.TicketMenu)
            {
                if (_citationLocation.Length < 30)
                    _citationLocation.Append(c);
            }
            else if (_currentScreen == ScreenMode.VehicleSelection)
            {
                if (_vehicleSearchInput.Length < 20)
                {
                    if (char.IsLetter(c))
                        _vehicleSearchInput.Append(char.ToUpper(c));
                    else
                        _vehicleSearchInput.Append(c);
                    UpdateVehicleSuggestions();
                }
            }
        }

        private void HandleBackspace()
        {
            if (_currentScreen == ScreenMode.Search)
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

        private void SelectVehicleSuggestion()
        {
            if (_selectedVehicleSuggestionIndex < 0 || _selectedVehicleSuggestionIndex >= _vehicleSuggestions.Count)
                return;

            string selected = _vehicleSuggestions[_selectedVehicleSuggestionIndex];

            // Remove the "→ " and " (OWNER)" markers if present
            string cleanSelected = selected.Replace("→ ", "").Replace(" (OWNER)", "");

            // Find the matching vehicle in the suggestions list
            var matchingVehicle = _vehicleSuggestionsList.FirstOrDefault(v => v.DisplayName == cleanSelected);

            if (matchingVehicle != null)
            {
                _selectedVehicle = new Dictionary<string, object>
                {
                    { "id", matchingVehicle.Id },
                    { "license_plate", matchingVehicle.LicensePlate },
                    { "vehicle_model", matchingVehicle.Model },
                    { "owner_name", matchingVehicle.OwnerName }
                };

                Game.LogTrivial($"[Computer] Selected vehicle: {matchingVehicle.LicensePlate} (ID: {matchingVehicle.Id})");

                // Now issue the ticket with the selected vehicle
                IssueSelectedTicketWithVehicle();
            }
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
            _currentVehicle = null;
            _showingOwnerPrompt = false;

            if (_currentSearchMode == SearchMode.Vehicle)
            {
                string plate = _vehiclePlateInput.ToString().Trim();
                if (string.IsNullOrEmpty(plate))
                {
                    _leftColumnResults.Add("~r~Please enter a license plate");
                    return;
                }

                Game.LogTrivial($"Searching for vehicle plate: {plate}");
                var result = _database.LookupByPlate(plate);

                if (result != null && result.Count > 0)
                {
                    _currentVehicle = result;

                    _leftColumnResults.Add($"=== VEHICLE RESULTS ===");

                    // Vehicle info
                    string vehicleType = GetSafeString(result, "owner_type");
                    string ownerName = GetSafeString(result, "owner_name");

                    _leftColumnResults.Add($"Plate: {GetSafeString(result, "license_plate")}");
                    _leftColumnResults.Add($"Model: {GetSafeString(result, "vehicle_model")}");

                    if (result.ContainsKey("color_primary"))
                        _leftColumnResults.Add($"Color: {GetSafeString(result, "color_primary")}/{GetSafeString(result, "color_secondary")}");

                    _leftColumnResults.Add($"Owner: {ownerName} ({vehicleType})");

                    if (result.ContainsKey("company_name") && result["company_name"] != null)
                        _leftColumnResults.Add($"Company: {GetSafeString(result, "company_name")}");

                    // Registration status
                    if (GetSafeInt(result, "no_registration") == 1)
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

                    // Insurance status
                    if (GetSafeInt(result, "no_insurance") == 1)
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

                    // Stolen status
                    if (GetSafeInt(result, "is_stolen") == 1)
                    {
                        string stolenReason = GetSafeString(result, "stolen_reason");
                        _leftColumnResults.Add($"~r~*** STOLEN VEHICLE ***");
                        _leftColumnResults.Add($"~r~Reason: {stolenReason}");
                    }

                    // Impounded status
                    if (GetSafeInt(result, "is_impounded") == 1)
                    {
                        string impoundReason = GetSafeString(result, "impounded_reason");
                        string impoundLocation = GetSafeString(result, "impounded_location");
                        _leftColumnResults.Add($"~r~*** IMPOUNDED ***");
                        _leftColumnResults.Add($"~r~Reason: {impoundReason}");
                        _leftColumnResults.Add($"~y~Location: {impoundLocation}");
                    }

                    // Owner information
                    if (!string.IsNullOrEmpty(ownerName))
                    {
                        _leftColumnResults.Add($"");
                        _leftColumnResults.Add($"=== OWNER INFORMATION ===");
                        _leftColumnResults.Add($"Name: {ownerName}");

                        string homeAddress = GetSafeString(result, "home_address");
                        if (!string.IsNullOrEmpty(homeAddress))
                            _leftColumnResults.Add($"Address: {homeAddress}");

                        string licenseStatus = GetSafeString(result, "license_status");
                        if (!string.IsNullOrEmpty(licenseStatus))
                        {
                            string statusColor = "~g~";
                            string statusUpper = licenseStatus.ToUpper();
                            if (statusUpper == "SUSPENDED") statusColor = "~y~";
                            if (statusUpper == "REVOKED") statusColor = "~r~";
                            if (statusUpper == "EXPIRED") statusColor = "~y~";

                            _leftColumnResults.Add($"License: {statusColor}{licenseStatus}~w~");
                        }
                    }

                    // Check if owner is wanted
                    if (GetSafeBool(result, "is_wanted"))
                    {
                        string wantedReason = GetSafeString(result, "wanted_reason");
                        _leftColumnResults.Add($"~r~OWNER IS WANTED: {wantedReason}");
                    }

                    // Check if owner is incarcerated
                    if (GetSafeBool(result, "is_incarcerated"))
                    {
                        _leftColumnResults.Add($"~r~OWNER IS INCARCERATED");
                    }

                    _leftColumnResults.Add($"");
                    _leftColumnResults.Add($"~g~[A/ENTER] (press twice) View Owner Info");
                    _leftColumnResults.Add($"~y~[Y/F6] Issue Ticket to Owner");

                    // Get person info if owner is a person
                    if (GetSafeInt(result, "ped_id") > 0)
                    {
                        int pedId = GetSafeInt(result, "ped_id");
                        string firstName = GetSafeString(result, "first_name");
                        string lastName = GetSafeString(result, "last_name");

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

                    // Set current screen to VehicleDetails to enable button actions
                    _currentScreen = ScreenMode.VehicleDetails;
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

                    string date = GetSafeString(ticket, "date_issued");
                    if (string.IsNullOrEmpty(date) || date.Length < 10)
                        date = "Unknown";
                    else if (date.Length > 10)
                        date = date.Substring(0, 10);

                    string offense = GetSafeString(ticket, "offense");
                    string fine = GetSafeString(ticket, "fine_amount");
                    string vehicle = GetSafeString(ticket, "license_plate");
                    string model = GetSafeString(ticket, "vehicle_model");

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

            try
            {
                var person = _lastPersonResults[_selectedResultIndex];

                if (person == null) return;

                string firstName = GetSafeString(person, "first_name");
                string lastName = GetSafeString(person, "last_name");

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

                // SAFE ACCESS WITH NULL CHECKS
                string safeFirstName = GetSafeString(person, "first_name");
                string safeLastName = GetSafeString(person, "last_name");
                string safeAddress = GetSafeString(person, "home_address");

                _leftColumnResults.Add($"Name: {safeFirstName} {safeLastName}");
                _leftColumnResults.Add($"Address: {safeAddress}");

                // SAFE LICENSE INFO
                string licenseNum = GetSafeString(person, "license_number");
                if (string.IsNullOrEmpty(licenseNum)) licenseNum = "None";

                string licenseStatus = GetSafeString(person, "license_status");
                if (string.IsNullOrEmpty(licenseStatus)) licenseStatus = "Valid";

                string licenseReason = GetSafeString(person, "license_reason");
                string licenseExpiry = GetSafeString(person, "license_expiry");
                string licenseClass = GetSafeString(person, "license_class");
                if (string.IsNullOrEmpty(licenseClass)) licenseClass = "Class C";

                string statusColor = "~g~";
                string statusUpper = licenseStatus.ToUpper();

                switch (statusUpper)
                {
                    case "SUSPENDED": statusColor = "~y~"; break;
                    case "REVOKED": statusColor = "~r~"; break;
                    case "EXPIRED": statusColor = "~y~"; break;
                    case "NOLICENSE":
                    case "NO LICENSE":
                    case "NONE":
                        statusColor = "~r~";
                        licenseNum = "None";
                        break;
                }

                _leftColumnResults.Add($"License: {licenseNum} {statusColor}{statusUpper}~w~");
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
                    (statusUpper == "SUSPENDED" || statusUpper == "REVOKED"))
                {
                    _leftColumnResults.Add($"Reason: {licenseReason}");
                }

                // WANTED STATUS
                bool isWanted = GetSafeBool(person, "is_wanted");
                if (isWanted)
                {
                    string wantedReason = GetSafeString(person, "wanted_reason");
                    _leftColumnResults.Add($"~r~*** WANTED ***");
                    _leftColumnResults.Add($"~r~Reason: {wantedReason}");

                    string wantedLastSeen = GetSafeString(person, "wanted_last_seen");
                    if (!string.IsNullOrEmpty(wantedLastSeen))
                    {
                        _leftColumnResults.Add($"~y~Last Seen: {wantedLastSeen}");
                    }
                    _leftColumnResults.Add($"");
                }

                // INCARCERATED STATUS
                bool isIncarcerated = GetSafeBool(person, "is_incarcerated");
                if (isIncarcerated)
                {
                    string incarceratedReason = GetSafeString(person, "incarcerated_reason");
                    int days = GetSafeInt(person, "incarcerated_days");
                    string releaseDate = GetSafeString(person, "release_date");

                    _leftColumnResults.Add($"~r~*** INCARCERATED ***");
                    _leftColumnResults.Add($"~r~Reason: {incarceratedReason}");
                    _leftColumnResults.Add($"~y~Sentence: {days} days");
                    _leftColumnResults.Add($"~y~Release: {releaseDate}");
                    _leftColumnResults.Add($"");
                }

                string employerName = GetSafeString(person, "employer_name");
                string jobTitle = GetSafeString(person, "job_title");
                if (!string.IsNullOrEmpty(employerName))
                    _leftColumnResults.Add($"Employer: {employerName} ({jobTitle})");

                // Display owned vehicles with new fields
                if (person.ContainsKey("owned_vehicles"))
                {
                    var vehicles = person["owned_vehicles"] as List<Dictionary<string, object>>;
                    if (vehicles != null && vehicles.Count > 0)
                    {
                        _leftColumnResults.Add($"Vehicles ({vehicles.Count}):");
                        foreach (var vehicle in vehicles)
                        {
                            if (vehicle == null) continue;

                            string plate = GetSafeString(vehicle, "license_plate");
                            string model = GetSafeString(vehicle, "vehicle_model");
                            string color1 = GetSafeString(vehicle, "color_primary");
                            string color2 = GetSafeString(vehicle, "color_secondary");

                            string colorText = string.IsNullOrEmpty(color1) ? "" : $" ({color1}";
                            colorText += string.IsNullOrEmpty(color2) ? "" : $"/{color2})";

                            _leftColumnResults.Add($"  • {plate}: {model}{colorText}");

                            // Show vehicle status flags
                            if (GetSafeInt(vehicle, "is_stolen") == 1)
                                _leftColumnResults.Add($"    ~r~[STOLEN]~w~");

                            if (GetSafeInt(vehicle, "is_impounded") == 1)
                                _leftColumnResults.Add($"    ~r~[IMPOUNDED]~w~");

                            if (GetSafeInt(vehicle, "no_registration") == 1)
                                _leftColumnResults.Add($"    ~r~[NO REGISTRATION]~w~");

                            if (GetSafeInt(vehicle, "no_insurance") == 1)
                                _leftColumnResults.Add($"    ~r~[NO INSURANCE]~w~");
                        }
                    }
                }

                _leftColumnResults.Add($"");
                _leftColumnResults.Add("~g~[A/ENTER/Y/F6] Issue Ticket");

                // Set current screen to PersonDetails to enable button actions
                _currentScreen = ScreenMode.PersonDetails;

                LoadCitationHistory(person);

                _activePersonField = PersonField.FirstName;

                // Log the values for debugging
                Game.LogTrivial($"[Computer] License Status: '{licenseStatus}', Upper: '{statusUpper}'");
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[Computer] CRITICAL ERROR in DisplaySelectedPerson: {ex.Message}");
                Game.LogTrivial($"[Computer] Stack trace: {ex.StackTrace}");
                _leftColumnResults.Add($"~r~Error displaying person: {ex.Message}");
            }
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

            string firstName = GetSafeString(_currentSelectedPerson, "first_name");
            string lastName = GetSafeString(_currentSelectedPerson, "last_name");
            Game.DisplayNotification($"Select charge for {firstName} {lastName}");
        }

        private void OpenVehicleSelection()
        {
            if (_currentSelectedPerson == null) return;

            _currentScreen = ScreenMode.VehicleSelection;
            _vehicleSearchInput.Clear();
            _selectedVehicle = null;
            UpdateVehicleSuggestions();
            _selectedVehicleSuggestionIndex = _vehicleSuggestions.Count > 0 ? 0 : -1;

            string firstName = GetSafeString(_currentSelectedPerson, "first_name");
            string lastName = GetSafeString(_currentSelectedPerson, "last_name");
            Game.DisplayNotification($"Select vehicle for {firstName} {lastName}");
        }

        private void IssueSelectedTicketWithVehicle()
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

            int pedId = GetSafeInt(_currentSelectedPerson, "id");
            string firstName = GetSafeString(_currentSelectedPerson, "first_name");
            string lastName = GetSafeString(_currentSelectedPerson, "last_name");
            string personName = $"{firstName} {lastName}".Trim();

            // Get vehicle ID (use 1 as fallback if no vehicle selected)
            int vehicleId = 1;
            string vehicleInfo = "No vehicle";

            if (_selectedVehicle != null && _selectedVehicle.ContainsKey("id"))
            {
                vehicleId = GetSafeInt(_selectedVehicle, "id");
                vehicleInfo = GetSafeString(_selectedVehicle, "license_plate");
                Game.LogTrivial($"[Computer] Issuing ticket with vehicle: {vehicleInfo} (ID: {vehicleId})");
            }
            else
            {
                Game.LogTrivial($"[Computer] Issuing ticket with default vehicle ID 1");
            }

            // Add the ticket to database
            _database.AddTicket(pedId, vehicleId, ticket.Description, ticket.Fine, location);

            if (ticket.IsArrestable)
            {
                // Also mark them as arrested/incarcerated in database
                _database.IncarceratePed(pedId, ticket.Description, ticket.JailDays, location);
                Game.DisplayNotification($"~r~ARREST~w~ issued to {personName} - {ticket.JailDays} days");
                Game.LogTrivial($"[ARREST] {personName} arrested for {ticket.Description}, {ticket.JailDays} days");
            }
            else
            {
                string vehicleMsg = vehicleId != 1 ? $" (Vehicle: {vehicleInfo})" : "";
                Game.DisplayNotification($"~g~Citation~w~ issued to {personName} - ${ticket.Fine}{vehicleMsg}");
                Game.LogTrivial($"[CITATION] {personName} cited for {ticket.Description}, ${ticket.Fine} - Vehicle: {vehicleInfo}");
            }

            // Return to search screen
            _currentScreen = ScreenMode.Search;
            _activePersonField = PersonField.FirstName;

            // Refresh the search results
            PerformSearch();
        }

        private void DrawComputerScreen()
        {
            float centerX = 0.5f;

            NativeFunction.Natives.DRAW_RECT(centerX, 0.5f, 0.7f, 0.85f, 0, 0, 0, 220);
            NativeFunction.Natives.DRAW_RECT(centerX, 0.1f, 0.7f, 0.05f, 0, 0, 100, 255);

            string headerText = "";
            if (_currentScreen == ScreenMode.TicketMenu)
                headerText = "SELECT CHARGE";
            else if (_currentScreen == ScreenMode.VehicleSelection)
                headerText = "SELECT VEHICLE";
            else if (_currentScreen == ScreenMode.PersonDetails)
                headerText = "PERSON DETAILS";
            else if (_currentScreen == ScreenMode.VehicleDetails)
                headerText = "VEHICLE DETAILS";
            else
                headerText = "PERSISTENT WORLD MDT";

            DrawText(centerX, 0.09f, headerText, 1.0f, 255, 255, 255, 255, true);

            if (_currentScreen == ScreenMode.TicketMenu)
            {
                DrawTicketMenu(centerX);
            }
            else if (_currentScreen == ScreenMode.VehicleSelection)
            {
                DrawVehicleSelection(centerX);
            }
            else if (_currentScreen == ScreenMode.PersonDetails || _currentScreen == ScreenMode.VehicleDetails)
            {
                DrawDetailsScreen(centerX);
            }
            else
            {
                DrawSearchScreen(centerX);
            }
        }

        private void DrawDetailsScreen(float centerX)
        {
            float leftColX = centerX - 0.3f;
            float rightColX = centerX + 0.05f;
            float resultsStartY = 0.18f;

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

            // Show prompt if waiting for second press
            if (_showingOwnerPrompt)
            {
                DrawText(centerX, 0.8f, "Press again to view owner", 0.4f, 255, 255, 0, 255, true);
            }

            float footerY = 0.88f;
            string footerText = "B Back | Y/F6 Ticket";
            if (_currentScreen == ScreenMode.VehicleDetails)
                footerText = "A/ENTER (twice) View Owner | Y/F6 Ticket | B Back";

            DrawText(centerX, footerY, footerText, 0.35f, 200, 200, 200, 255, true);
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
                string firstName = GetSafeString(_currentSelectedPerson, "first_name");
                string lastName = GetSafeString(_currentSelectedPerson, "last_name");
                DrawText(centerX - 0.3f, yPos, $"Issuing to: {firstName} {lastName}", 0.5f, 255, 255, 0, 255, false);
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

            // Show next step hint
            DrawText(centerX - 0.28f, yPos + 0.03f, "A/ENTER to select vehicle", 0.35f, 0, 255, 0, 255, false);

            float footerY = 0.88f;
            DrawText(centerX, footerY, "↑↓/DPad Navigate | X/TAB Switch | A/ENTER Next | B Back | Type Location", 0.35f, 200, 200, 200, 255, true);
        }

        private void DrawVehicleSelection(float centerX)
        {
            float yPos = 0.18f;

            if (_currentSelectedPerson != null)
            {
                string firstName = GetSafeString(_currentSelectedPerson, "first_name");
                string lastName = GetSafeString(_currentSelectedPerson, "last_name");
                DrawText(centerX - 0.3f, yPos, $"Select vehicle for: {firstName} {lastName}", 0.5f, 255, 255, 0, 255, false);
                yPos += 0.05f;
            }

            DrawText(centerX - 0.3f, yPos, "Search:", 0.5f, 200, 200, 200, 255, false);
            string searchDisplay = _vehicleSearchInput.ToString();
            if (_cursorVisible)
                searchDisplay += "_";
            else
                searchDisplay += " ";
            DrawText(centerX, yPos, searchDisplay, 0.5f, 0, 255, 255, 255, false);
            yPos += 0.05f;

            // Draw vehicle suggestions
            if (_showVehicleSuggestions)
            {
                DrawText(centerX - 0.3f, yPos, "Vehicles:", 0.4f, 255, 255, 0, 255, false);
                yPos += 0.04f;

                for (int i = 0; i < _vehicleSuggestions.Count && i < 8; i++)
                {
                    string prefix = (i == _selectedVehicleSuggestionIndex) ? "→ " : "  ";
                    int r = (i == _selectedVehicleSuggestionIndex) ? 0 : 255;
                    int g = (i == _selectedVehicleSuggestionIndex) ? 255 : 255;
                    int b = (i == _selectedVehicleSuggestionIndex) ? 0 : 255;

                    string suggestion = _vehicleSuggestions[i];
                    if (suggestion.Length > 35)
                        suggestion = suggestion.Substring(0, 32) + "...";

                    DrawText(centerX - 0.28f, yPos, prefix + suggestion, 0.4f, r, g, b, 255, false);
                    yPos += 0.03f;
                }
            }
            else
            {
                DrawText(centerX - 0.3f, yPos, "No vehicles found", 0.4f, 255, 0, 0, 255, false);
                yPos += 0.04f;
            }

            // Show selection hint
            if (_selectedVehicle != null)
            {
                string plate = GetSafeString(_selectedVehicle, "license_plate");
                DrawText(centerX - 0.28f, yPos + 0.03f, $"Selected: {plate}", 0.35f, 0, 255, 0, 255, false);
            }

            float footerY = 0.88f;
            DrawText(centerX, footerY, "↑↓/DPad Navigate | A/ENTER Select | B Back | Type to search", 0.35f, 200, 200, 200, 255, true);
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

            // Clear key state dictionaries
            _lastKeyStates.Clear();
            _lastControllerStates.Clear();

            // Unfreeze the suspect if there was one
            if (_currentSuspect != null && _currentSuspect.Exists())
            {
                NativeFunction.Natives.FREEZE_ENTITY_POSITION(_currentSuspect, false);
                _currentSuspect.BlockPermanentEvents = false;
                _currentSuspect.KeepTasks = false;
            }

            // Unfreeze the suspect vehicle
            if (_currentSuspectVehicle != null && _currentSuspectVehicle.Exists())
            {
                NativeFunction.Natives.FREEZE_ENTITY_POSITION(_currentSuspectVehicle, false);
            }

            // Unfreeze audio
            FreezeAudio(false);

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