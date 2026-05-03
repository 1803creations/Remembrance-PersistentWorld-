using System;
using System.Linq;
using System.Windows.Forms;
using Rage;
using Rage.Native;
using LSPD_First_Response.Mod.API;
using LSPD_First_Response.Engine.Scripting.Entities;
using PersistentWorld.Database;

namespace PersistentWorld
{
    /// <summary>
    /// Lightweight in-game dispatch menu integrated with PersistentWorld.
    /// Opens when D-Pad Up is pressed three times within 5 seconds.
    /// </summary>
    public class DispatchMenu
    {
        private readonly DatabaseManager _db;
        private readonly Config _config;

        private bool _open = false;
        private int _selectedIndex = 0;
        private DateTime _lastInput = DateTime.Now;
        private DateTime _lastToggle = DateTime.MinValue;
        private const int INPUT_DEBOUNCE_MS = 120;

        // Triple-press detection
        private int _dpadUpCount = 0;
        private DateTime _firstPressTime = DateTime.MinValue;
        private readonly TimeSpan _tripleWindow = TimeSpan.FromSeconds(5);

        private readonly string[] _options = new[] { "Vehicle Check", "Person Check" };

        public DispatchMenu(DatabaseManager db, Config config)
        {
            _db = db;
            _config = config;
        }

        public void Update()
        {
            HandleOpenGesture();
            if (_open)
            {
                HandleMenuInput();
                DrawMenu();
            }
        }

        private void HandleOpenGesture()
        {
            bool dpadUpPressed = CtrlPressed(GameControl.ScriptRUp)
                              || CtrlPressed(GameControl.FrontendUp)
                              || CtrlPressed(GameControl.ScriptPadUp);
            bool keyboardT = Game.IsKeyDownRightNow(_config.DispatchOpenKey);
            bool toggleAllowed = (DateTime.Now - _lastToggle).TotalMilliseconds > 500;

            if (keyboardT && toggleAllowed)
            {
                Toggle(); // immediate toggle with T
                _lastToggle = DateTime.Now;
                _dpadUpCount = 0;
                _firstPressTime = DateTime.MinValue;
                return;
            }

            if (dpadUpPressed || CtrlPressed(_config.DispatchOpenControl)) // D-Pad Up (or configured)
            {
                if (_firstPressTime == DateTime.MinValue || (DateTime.Now - _firstPressTime) > _tripleWindow)
                {
                    _firstPressTime = DateTime.Now;
                    _dpadUpCount = 1;
                }
                else
                {
                    _dpadUpCount++;
                    if (_dpadUpCount >= 3 && toggleAllowed)
                    {
                        Toggle();
                        _lastToggle = DateTime.Now;
                        _dpadUpCount = 0;
                        _firstPressTime = DateTime.MinValue;
                    }
                }
            }
        }

        private void Toggle()
        {
            _open = !_open;
            _selectedIndex = 0;
            if (_open)
            {
                Game.DisplayHelp("Dispatch: ↑/↓ select • A/ENTER run • B/Back close");
            }
        }

        private bool CtrlPressed(GameControl control)
        {
            return NativeFunction.Natives.IS_CONTROL_JUST_PRESSED<bool>(0, (int)control);
        }

        private void HandleMenuInput()
        {
            bool up = CtrlPressed(GameControl.ScriptPadUp) || CtrlPressed(GameControl.FrontendUp);
            bool down = CtrlPressed(GameControl.ScriptPadDown) || CtrlPressed(GameControl.FrontendDown);
            bool select = CtrlPressed(_config.DispatchSelectControl);
            bool back = CtrlPressed(_config.DispatchBackControl);

            // Keyboard fallbacks
            if (Game.IsKeyDownRightNow(_config.DispatchSelectKey)) select = true;
            if (Game.IsKeyDownRightNow(_config.DispatchBackKey)) back = true;
            if (Game.IsKeyDownRightNow(_config.DispatchUpKey)) up = true;
            if (Game.IsKeyDownRightNow(_config.DispatchDownKey)) down = true;

            if (up)
            {
                if ((DateTime.Now - _lastInput).TotalMilliseconds < INPUT_DEBOUNCE_MS) return;
                _lastInput = DateTime.Now;
                _selectedIndex = (_selectedIndex - 1 + _options.Length) % _options.Length;
            }
            else if (down)
            {
                if ((DateTime.Now - _lastInput).TotalMilliseconds < INPUT_DEBOUNCE_MS) return;
                _lastInput = DateTime.Now;
                _selectedIndex = (_selectedIndex + 1) % _options.Length;
            }
            else if (back)
            {
                _open = false;
            }
            else if (select || Game.IsKeyDownRightNow(Keys.Enter) || Game.IsKeyDownRightNow(Keys.NumPad5))
            {
                RunSelection();
                _open = false;
            }
        }

        private void DrawMenu()
        {
            float x = 0.15f;
            float y = 0.25f;
            float width = 0.22f;
            float line = 0.035f;

            NativeFunction.Natives.DRAW_RECT(x + width / 2f, y + 0.02f, width, 0.05f, 0, 50, 120, 220);
            DrawText(x + width / 2f, y, "DISPATCH", 0.55f, 255, 255, 255, 255, true);

            for (int i = 0; i < _options.Length; i++)
            {
                float rowY = y + 0.07f + i * line;
                int r = i == _selectedIndex ? 0 : 200;
                int g = i == _selectedIndex ? 200 : 200;
                int b = i == _selectedIndex ? 255 : 200;
                NativeFunction.Natives.DRAW_RECT(x + width / 2f, rowY + 0.012f, width, line, 0, 0, 0, 120);
                DrawText(x + 0.01f, rowY, _options[i], 0.45f, r, g, b, 255, false);
            }

            DrawText(x + width / 2f, y + 0.17f, "A/ENTER Run | B Close", 0.32f, 180, 180, 180, 255, true);
        }

        private void RunSelection()
        {
            if (_selectedIndex == 0)
                VehicleCheck();
            else
                PersonCheck();
        }

        private void VehicleCheck()
        {
            var vehicle = GetVehicleAhead();
            if (vehicle == null)
            {
                Game.DisplayNotification("~y~Dispatch: No vehicle ahead.");
                return;
            }

            string plate = (vehicle.LicensePlate ?? "").Trim().ToUpper();
            if (string.IsNullOrWhiteSpace(plate))
            {
                Game.DisplayNotification("~y~Dispatch: Unable to read plate.");
                return;
            }

            var record = _db.LookupByPlate(plate);
            if (record == null || record.Count == 0)
            {
                Game.DisplayNotification($"~b~Dispatch:~w~ {plate} not found.");
                return;
            }

            string model = SafeStr(record, "vehicle_model", "Unknown");
            string ownerName = SafeStr(record, "owner_name", "Unknown");
            bool stolen = SafeBool(record, "is_stolen");
            bool noReg = SafeBool(record, "no_registration");
            bool noIns = SafeBool(record, "no_insurance");
            string regExp = SafeStr(record, "registration_expiry", "");
            string insExp = SafeStr(record, "insurance_expiry", "");

            // Owner license status
            string licenseStatus = SafeStr(record, "license_status", "");
            string licenseExpiry = SafeStr(record, "license_expiry", "");
            bool wanted = SafeBool(record, "is_wanted");

            if (SafeStr(record, "owner_type", "") == "person" && record.TryGetValue("owner_id", out var oid) && oid != null)
            {
                if (int.TryParse(oid.ToString(), out int ownerId))
                {
                    var owner = _db.LookupPersonById(ownerId);
                    if (owner != null)
                    {
                        licenseStatus = SafeStr(owner, "license_status", licenseStatus);
                        licenseExpiry = SafeStr(owner, "license_expiry", licenseExpiry);
                        wanted = SafeBool(owner, "is_wanted") || wanted;
                    }
                }
            }

            string licCategory = "Unknown";
            if (wanted) licCategory = "~r~WARRANT";
            else if (!string.IsNullOrEmpty(licenseStatus) && licenseStatus.IndexOf("revok", StringComparison.OrdinalIgnoreCase) >= 0) licCategory = "~r~REVOKED";
            else if (!string.IsNullOrEmpty(licenseStatus) && licenseStatus.IndexOf("susp", StringComparison.OrdinalIgnoreCase) >= 0) licCategory = "~o~SUSPENDED";
            else if (IsExpired(licenseExpiry)) licCategory = "~o~EXPIRED";
            else if (!string.IsNullOrEmpty(licenseStatus)) licCategory = "~g~CLEAN";

            string regStatus = noReg ? "~r~NONE" : (IsExpired(regExp) ? "~o~EXPIRED" : "~g~OK");
            string insStatus = noIns ? "~r~NONE" : (IsExpired(insExp) ? "~o~EXPIRED" : "~g~OK");
            string stolenText = stolen ? "~r~STOLEN" : "~g~Clear";

            Game.DisplayNotification($"~b~Dispatch:~w~ {plate} ({model})\nOwner: {ownerName}\nReg: {regStatus} ~w~/ Ins: {insStatus}\nLicense: {licCategory}~w~ ({licenseStatus})\nStolen: {stolenText}");
        }

        private void PersonCheck()
        {
            var ped = GetPedNearby();
            if (ped == null)
            {
                Game.DisplayNotification("~y~Dispatch: No nearby person.");
                return;
            }

            var persona = Functions.GetPersonaForPed(ped);
            if (persona == null)
            {
                Game.DisplayNotification("~y~Dispatch: Persona unavailable.");
                return;
            }

            var record = _db.LookupByName(persona.Forename, persona.Surname)?.FirstOrDefault();
            if (record == null)
            {
                Game.DisplayNotification($"~b~Dispatch:~w~ {persona.Forename} {persona.Surname} not found.");
                return;
            }

            string licenseStatus = SafeStr(record, "license_status", "Unknown");
            bool wanted = SafeBool(record, "is_wanted");
            string warrantInfo = wanted ? SafeStr(record, "wanted_reason", "Active warrant") : "None";
            string dob = SafeStr(record, "date_of_birth", "");

            Game.DisplayNotification($"~b~Dispatch:~w~ {persona.Forename} {persona.Surname}\nDOB: {dob}\nLicense: {licenseStatus}\nWarrants: {warrantInfo}");
        }

        private Vehicle GetVehicleAhead(float maxDistance = 15f)
        {
            Ped player = Game.LocalPlayer.Character;
            var ahead = player.GetOffsetPositionFront(maxDistance * 0.5f);
            var entity = World.GetClosestEntity(ahead, maxDistance, GetEntitiesFlags.ConsiderAllVehicles);
            var vehicle = entity as Vehicle;
            if (vehicle != null && vehicle.Exists())
            {
                var toVehicle = (vehicle.Position - player.Position);
                toVehicle.Normalize();
                float dot = Vector3.Dot(toVehicle, player.ForwardVector);
                if (dot > 0.2f) return vehicle;
            }
            return null;
        }

        private Ped GetPedNearby(float maxDistance = 8f)
        {
            Ped player = Game.LocalPlayer.Character;
            var entity = World.GetClosestEntity(player.Position, maxDistance, GetEntitiesFlags.ConsiderAllPeds);
            var ped = entity as Ped;
            if (ped != null && ped.Exists() && ped != player && !ped.IsDead) return ped;
            return null;
        }

        private bool IsExpired(string dateString)
        {
            if (string.IsNullOrWhiteSpace(dateString)) return false;
            if (DateTime.TryParse(dateString, out DateTime parsed))
            {
                return parsed.Date < DateTime.Now.Date;
            }
            return false;
        }

        private string SafeStr(System.Collections.Generic.Dictionary<string, object> dict, string key, string def)
        {
            if (dict != null && dict.TryGetValue(key, out var v) && v != null) return v.ToString();
            return def;
        }

        private bool SafeBool(System.Collections.Generic.Dictionary<string, object> dict, string key)
        {
            if (dict != null && dict.TryGetValue(key, out var v) && v != null)
            {
                if (v is bool b) return b;
                if (int.TryParse(v.ToString(), out int i)) return i != 0;
            }
            return false;
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
    }
}
