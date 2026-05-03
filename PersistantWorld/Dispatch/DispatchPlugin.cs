using System;
using System.Windows.Forms;
using Rage;
using Rage.Native;
using LSPD_First_Response.Mod.API;
using LSPD_First_Response.Engine.Scripting.Entities;
using PersistentWorld.Database;

namespace PersistentWorld.Dispatch
{
    public class DispatchPlugin : Plugin
    {
        private DatabaseManager _database;
        private bool _onDuty;
        private bool _menuOpen;
        private DateTime _lastInput = DateTime.Now;
        private readonly Keys _toggleKey = Keys.F7;
        private const int INPUT_DEBOUNCE_MS = 250;

        public override void Initialize()
        {
            Game.LogTrivial("[Dispatch] Initializing...");

            try
            {
                string gtaPath = AppDomain.CurrentDomain.BaseDirectory;
                string dbPath = System.IO.Path.Combine(
                    gtaPath,
                    "Plugins",
                    "LSPDFR",
                    "PersistentWorld",
                    "PersistentWorld.db");

                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dbPath));

                _database = new DatabaseManager(dbPath);
                _database.InitializeDatabase();

                _onDuty = true; // start enabled; updated when LSPDFR signals duty change
                Functions.OnOnDutyStateChanged += OnDutyStateChanged;

                GameFiber.StartNew(MenuLoop);

                Game.DisplayNotification("~b~Dispatch~w~ online. Press ~y~F7~w~ for checks.");
                Game.LogTrivial("[Dispatch] Loaded successfully");
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[Dispatch] Failed to initialize: {ex.Message}");
                Game.DisplayNotification("~r~Dispatch failed to load. Check log.");
            }
        }

        public override void Finally()
        {
            Functions.OnOnDutyStateChanged -= OnDutyStateChanged;
            _database?.Dispose();
            Game.LogTrivial("[Dispatch] Unloaded");
        }

        private void OnDutyStateChanged(bool onDuty)
        {
            _onDuty = onDuty;
            if (_onDuty)
            {
                Game.DisplayNotification("~b~Dispatch~w~ ready for checks.");
            }
            else
            {
                _menuOpen = false;
            }
        }

        private void MenuLoop()
        {
            while (true)
            {
                GameFiber.Yield();
                if (!_onDuty) continue;

                if (IsPressed(_toggleKey))
                {
                    _menuOpen = !_menuOpen;
                    if (_menuOpen)
                    {
                        Game.DisplayHelp("Dispatch: ~INPUT_FRONTEND_ACCEPT~ Vehicle Check | ~INPUT_FRONTEND_X~ Person Check | ~INPUT_FRONTEND_CANCEL~ Close");
                    }
                }

                if (!_menuOpen) continue;

                // Vehicle check mapped to Enter/NumPad1
                if (IsPressed(Keys.NumPad1) || Game.IsControlJustPressed(0, GameControl.FrontendAccept))
                {
                    PerformVehicleCheck();
                }

                // Person check mapped to NumPad2 / frontend X
                if (IsPressed(Keys.NumPad2) || Game.IsControlJustPressed(0, GameControl.FrontendX))
                {
                    PerformPersonCheck();
                }

                if (IsPressed(Keys.Back) || Game.IsControlJustPressed(0, GameControl.FrontendCancel))
                {
                    _menuOpen = false;
                }
            }
        }

        private bool IsPressed(Keys key)
        {
            if (!Game.IsKeyDown(key)) return false;
            if ((DateTime.Now - _lastInput).TotalMilliseconds < INPUT_DEBOUNCE_MS) return false;
            _lastInput = DateTime.Now;
            return true;
        }

        private Vehicle GetVehicleAhead(float maxDistance = 8f)
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

        private Ped GetPedNearby(float maxDistance = 4f)
        {
            Ped player = Game.LocalPlayer.Character;
            var entity = World.GetClosestEntity(player.Position, maxDistance, GetEntitiesFlags.ConsiderAllPeds);
            var ped = entity as Ped;
            if (ped != null && ped.Exists() && ped != player && !ped.IsDead) return ped;
            return null;
        }

        private void PerformVehicleCheck()
        {
            try
            {
                var vehicle = GetVehicleAhead();
                if (vehicle == null)
                {
                    Game.DisplayNotification("~y~Dispatch: No vehicle in front.");
                    return;
                }

                string plate = (vehicle.LicensePlate ?? "").Trim().ToUpper();
                if (string.IsNullOrWhiteSpace(plate))
                {
                    Game.DisplayNotification("~y~Dispatch: Unable to read plate.");
                    return;
                }

                var record = _database.LookupByPlate(plate);
                if (record == null || record.Count == 0)
                {
                    Game.DisplayNotification($"~b~Dispatch:~w~ Plate ~y~{plate}~w~ not found.");
                    return;
                }

                string model = record.ContainsKey("vehicle_model") ? record["vehicle_model"]?.ToString() ?? "Unknown" : "Unknown";
                string ownerName = record.ContainsKey("owner_name") ? record["owner_name"]?.ToString() ?? "Unknown" : "Unknown";
                bool stolen = record.TryGetValue("is_stolen", out var stolenVal) && Convert.ToInt32(stolenVal) == 1;
                bool noReg = record.TryGetValue("no_registration", out var nr) && Convert.ToInt32(nr) == 1;
                bool noIns = record.TryGetValue("no_insurance", out var ni) && Convert.ToInt32(ni) == 1;

                string regExp = record.TryGetValue("registration_expiry", out var reg) ? reg?.ToString() ?? "" : "";
                string insExp = record.TryGetValue("insurance_expiry", out var ins) ? ins?.ToString() ?? "" : "";

                string ownerType = record.TryGetValue("owner_type", out var ot) ? ot?.ToString() : "";
                string licenseStatus = "Unknown";
                string warrantInfo = "None";

                if (ownerType == "person" && record.TryGetValue("owner_id", out var oid) && oid != null)
                {
                    var owner = _database.LookupPersonById(Convert.ToInt32(oid));
                    if (owner != null)
                    {
                        licenseStatus = owner.TryGetValue("license_status", out var ls) ? ls?.ToString() ?? "Unknown" : "Unknown";
                        bool wanted = owner.TryGetValue("is_wanted", out var w) && Convert.ToInt32(w) == 1;
                        warrantInfo = wanted ? (owner.TryGetValue("wanted_reason", out var wr) ? wr?.ToString() ?? "Active warrant" : "Active warrant") : "None";
                        ownerName = $"{owner.GetValueOrDefault("first_name")} {owner.GetValueOrDefault("last_name")}".Trim();
                    }
                }

                string regStatus = noReg ? "~r~NONE" : (IsExpired(regExp) ? "~o~EXPIRED" : "~g~OK");
                string insStatus = noIns ? "~r~NONE" : (IsExpired(insExp) ? "~o~EXPIRED" : "~g~OK");
                string stolenText = stolen ? "~r~STOLEN" : "~g~Clear";

                Game.DisplayNotification($"~b~Dispatch:~w~ {plate} ({model})\nOwner: {ownerName}\nReg: {regStatus} ~w~/ Ins: {insStatus}\nStolen: {stolenText}\nLicense: {licenseStatus}\nWarrants: {warrantInfo}");
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[Dispatch] Vehicle check error: {ex.Message}");
                Game.DisplayNotification("~r~Dispatch error during vehicle check.");
            }
        }

        private void PerformPersonCheck()
        {
            try
            {
                var ped = GetPedNearby();
                if (ped == null)
                {
                    Game.DisplayNotification("~y~Dispatch: No nearby person.");
                    return;
                }

                Persona persona = Functions.GetPersonaForPed(ped);
                if (persona == null)
                {
                    Game.DisplayNotification("~y~Dispatch: Persona unavailable.");
                    return;
                }

                var record = _database.LookupPersonByName(persona.Forename, persona.Surname);
                if (record == null)
                {
                    Game.DisplayNotification($"~b~Dispatch:~w~ {persona.Forename} {persona.Surname} not found.");
                    return;
                }

                string licenseStatus = record.TryGetValue("license_status", out var ls) ? ls?.ToString() ?? "Unknown" : "Unknown";
                bool wanted = record.TryGetValue("is_wanted", out var w) && Convert.ToInt32(w) == 1;
                string warrantInfo = wanted ? (record.TryGetValue("wanted_reason", out var wr) ? wr?.ToString() ?? "Active warrant" : "Active warrant") : "None";
                string dob = record.TryGetValue("date_of_birth", out var dobVal) ? dobVal?.ToString() ?? "" : "";

                Game.DisplayNotification($"~b~Dispatch:~w~ {persona.Forename} {persona.Surname}\nDOB: {dob}\nLicense: {licenseStatus}\nWarrants: {warrantInfo}");
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[Dispatch] Person check error: {ex.Message}");
                Game.DisplayNotification("~r~Dispatch error during person check.");
            }
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
    }

    internal static class DictionaryExtensions
    {
        public static object GetValueOrDefault(this System.Collections.Generic.Dictionary<string, object> dict, string key)
        {
            return dict != null && dict.TryGetValue(key, out var value) ? value : null;
        }
    }
}
