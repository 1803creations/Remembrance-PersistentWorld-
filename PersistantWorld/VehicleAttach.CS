using System;
using System.Collections.Generic;
using System.Linq;
using Rage;
using Rage.Native;
using LSPD_First_Response.Mod.API;
using LSPD_First_Response.Engine.Scripting.Entities;
using PersistentWorld.Database;

namespace PersistentWorld.Vehicles
{
    public class VehicleAttach
    {
        private DatabaseManager _database;
        private bool _enabled = false;
        private List<string> _processedVehicles = new List<string>();
        private DateTime _lastCleanup = DateTime.Now;
        private Random _random = new Random();

        // Model tracking
        private Dictionary<string, string> _vehiclesNeedingModelUpdate = new Dictionary<string, string>();
        private DateTime _lastBatchUpdate = DateTime.Now;
        private const int BATCH_UPDATE_INTERVAL_SECONDS = 30; // Update every 30 seconds

        // For debugging/statistics
        private int _vehiclesProcessed = 0;
        private int _modelsUpdated = 0;

        public VehicleAttach(DatabaseManager database)
        {
            _database = database;
            Game.LogTrivial("[VehicleAttach] Initialized");
        }

        public void Enable()
        {
            _enabled = true;
            _processedVehicles.Clear();
            _vehiclesNeedingModelUpdate.Clear();
            Game.LogTrivial("[VehicleAttach] Enabled");
        }

        public void Disable()
        {
            _enabled = false;
            Game.LogTrivial("[VehicleAttach] Disabled");
        }

        public void Update()
        {
            if (!_enabled) return;

            try
            {
                // Process vehicles
                ProcessVehicles();

                // Process model updates in batches
                if ((DateTime.Now - _lastBatchUpdate).TotalSeconds >= BATCH_UPDATE_INTERVAL_SECONDS)
                {
                    ProcessModelUpdates();
                    _lastBatchUpdate = DateTime.Now;
                }

                // Cleanup processed vehicle list every 5 minutes
                if ((DateTime.Now - _lastCleanup).TotalMinutes >= 5)
                {
                    _processedVehicles.Clear();
                    _lastCleanup = DateTime.Now;
                    Game.LogTrivial($"[VehicleAttach] Cleaned processed vehicles cache. Total processed this session: {_vehiclesProcessed}, Models updated: {_modelsUpdated}");
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[VehicleAttach] Error in Update: {ex.Message}");
            }
        }

        private void ProcessVehicles()
        {
            try
            {
                // Get all vehicles within 200m of player
                var vehicles = World.GetAllVehicles()
                    .Where(v => v.Exists() && v.DistanceTo(Game.LocalPlayer.Character) < 200f)
                    .ToList();

                foreach (var vehicle in vehicles)
                {
                    ProcessVehicle(vehicle);
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[VehicleAttach] Error processing vehicles: {ex.Message}");
            }
        }

        private void ProcessVehicle(Vehicle vehicle)
        {
            try
            {
                if (!vehicle.Exists()) return;

                string plate = vehicle.LicensePlate;
                if (string.IsNullOrEmpty(plate) || plate.Length < 3) return;

                // Clean the plate (remove spaces, standardize format)
                plate = CleanPlate(plate);

                // Skip if we've already processed this vehicle recently
                if (_processedVehicles.Contains(plate))
                    return;

                // Check if vehicle exists in database
                var dbVehicle = _database.GetVehicleByPlate(plate);

                if (dbVehicle != null)
                {
                    // Vehicle exists in database
                    _vehiclesProcessed++;

                    // Check if model matches and update if needed
                    CheckAndQueueModelUpdate(vehicle, plate, dbVehicle);

                    // Check if plate needs to be enforced (if database plate differs)
                    string dbPlate = dbVehicle.ContainsKey("license_plate") ? dbVehicle["license_plate"].ToString() : "";

                    if (!string.IsNullOrEmpty(dbPlate) && dbPlate != plate)
                    {
                        Game.LogTrivial($"[VehicleAttach] CHANGING plate from '{plate}' to '{dbPlate}'");
                        vehicle.LicensePlate = dbPlate;

                        // Add the new plate to processed list to avoid reprocessing
                        if (!_processedVehicles.Contains(dbPlate))
                            _processedVehicles.Add(dbPlate);
                    }

                    // Mark as processed
                    if (!_processedVehicles.Contains(plate))
                        _processedVehicles.Add(plate);

                    // Get driver and update wanted status if needed
                    Ped driver = vehicle.Driver;
                    if (driver.Exists() && driver.IsHuman)
                    {
                        // Check if driver should be wanted
                        string driverName = GetPedName(driver);
                        if (!string.IsNullOrEmpty(driverName))
                        {
                            // Split name into first/last
                            string[] nameParts = driverName.Split(' ');
                            if (nameParts.Length >= 2)
                            {
                                string firstName = nameParts[0];
                                string lastName = string.Join(" ", nameParts, 1, nameParts.Length - 1);

                                var person = _database.LookupByName(firstName, lastName);
                                if (person != null && person.Count > 0)
                                {
                                    bool isWanted = person[0].ContainsKey("is_wanted") ? Convert.ToBoolean(person[0]["is_wanted"]) : false;
                                    if (isWanted)
                                    {
                                        Game.LogTrivial($"[VehicleAttach] WANTED PERSON: {driverName} driving {plate}");
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Vehicle not in database - this is normal for random traffic
                    // You could optionally add it to database here if desired
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[VehicleAttach] Error processing vehicle: {ex.Message}");
            }
        }

        private void CheckAndQueueModelUpdate(Vehicle vehicle, string plate, Dictionary<string, object> dbVehicle)
        {
            try
            {
                // Get model from database
                string dbModel = dbVehicle.ContainsKey("vehicle_model") ? dbVehicle["vehicle_model"].ToString() : "";

                // Get current vehicle model
                string currentModel = vehicle.Model.Name?.ToLower() ?? "";

                if (string.IsNullOrEmpty(dbModel) || string.IsNullOrEmpty(currentModel))
                    return;

                // Compare models (case-insensitive)
                if (!dbModel.Equals(currentModel, StringComparison.OrdinalIgnoreCase))
                {
                    Game.LogTrivial($"[VehicleAttach] Model mismatch for {plate}: DB='{dbModel}', Current='{currentModel}'");

                    // Add to update queue
                    lock (_vehiclesNeedingModelUpdate)
                    {
                        if (!_vehiclesNeedingModelUpdate.ContainsKey(plate))
                        {
                            _vehiclesNeedingModelUpdate.Add(plate, currentModel);
                            Game.LogTrivial($"[VehicleAttach] Queued model update for {plate}: {dbModel} -> {currentModel}");
                        }
                        else
                        {
                            // Update if different from queued value
                            if (_vehiclesNeedingModelUpdate[plate] != currentModel)
                            {
                                _vehiclesNeedingModelUpdate[plate] = currentModel;
                                Game.LogTrivial($"[VehicleAttach] Updated queued model for {plate} to {currentModel}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[VehicleAttach] Error checking model: {ex.Message}");
            }
        }

        private void ProcessModelUpdates()
        {
            try
            {
                if (_vehiclesNeedingModelUpdate.Count == 0)
                    return;

                Dictionary<string, string> updatesToProcess = null;

                lock (_vehiclesNeedingModelUpdate)
                {
                    updatesToProcess = new Dictionary<string, string>(_vehiclesNeedingModelUpdate);
                    _vehiclesNeedingModelUpdate.Clear();
                }

                if (updatesToProcess.Count > 0)
                {
                    Game.LogTrivial($"[VehicleAttach] Processing batch update of {updatesToProcess.Count} vehicle models");

                    // Update each vehicle individually
                    foreach (var update in updatesToProcess)
                    {
                        _database.UpdateVehicleModel(update.Key, update.Value);
                        _modelsUpdated++;
                    }

                    Game.LogTrivial($"[VehicleAttach] Batch update completed. Total models updated this session: {_modelsUpdated}");
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[VehicleAttach] Error processing model updates: {ex.Message}");
            }
        }

        // Force an immediate model update for a specific vehicle (useful for stopped vehicles)
        public void ForceModelUpdate(string licensePlate, string model)
        {
            try
            {
                if (string.IsNullOrEmpty(licensePlate) || string.IsNullOrEmpty(model))
                    return;

                _database.UpdateVehicleModel(licensePlate, model);
                Game.LogTrivial($"[VehicleAttach] Forced model update for {licensePlate} to {model}");

                // Remove from queue if present
                lock (_vehiclesNeedingModelUpdate)
                {
                    if (_vehiclesNeedingModelUpdate.ContainsKey(licensePlate))
                        _vehiclesNeedingModelUpdate.Remove(licensePlate);
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[VehicleAttach] Error forcing model update: {ex.Message}");
            }
        }

        // Force a full scan of all nearby vehicles
        public void ForceScan()
        {
            try
            {
                Game.LogTrivial("[VehicleAttach] Force scanning all vehicles");
                _processedVehicles.Clear(); // Clear cache to force reprocessing

                var vehicles = World.GetAllVehicles()
                    .Where(v => v.Exists() && v.DistanceTo(Game.LocalPlayer.Character) < 300f)
                    .ToList();

                Game.LogTrivial($"[VehicleAttach] Found {vehicles.Count} vehicles to scan");

                foreach (var vehicle in vehicles)
                {
                    ProcessVehicle(vehicle);
                }

                // Process any pending model updates immediately
                ProcessModelUpdates();
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[VehicleAttach] Error in force scan: {ex.Message}");
            }
        }

        // Clean plate string (remove spaces, ensure format)
        private string CleanPlate(string plate)
        {
            if (string.IsNullOrEmpty(plate))
                return plate;

            // Remove spaces and convert to uppercase
            return plate.Replace(" ", "").ToUpper();
        }

        // Get ped name from Persona if available
        private string GetPedName(Ped ped)
        {
            try
            {
                if (!ped.Exists()) return "";

                // Try to get from LSPDFR Persona
                var persona = Functions.GetPersonaForPed(ped);
                if (persona != null)
                {
                    // Persona uses Forename and Surname, not FirstName/LastName
                    return $"{persona.Forename} {persona.Surname}".Trim();
                }

                return "";
            }
            catch
            {
                return "";
            }
        }

        // Clear processed vehicles cache
        public void ClearProcessedCache()
        {
            _processedVehicles.Clear();
            Game.LogTrivial("[VehicleAttach] Processed vehicles cache cleared");
        }

        // Get statistics
        public int GetProcessedCount()
        {
            return _vehiclesProcessed;
        }

        public int GetModelUpdateCount()
        {
            return _modelsUpdated;
        }

        public int GetQueuedModelUpdateCount()
        {
            lock (_vehiclesNeedingModelUpdate)
            {
                return _vehiclesNeedingModelUpdate.Count;
            }
        }
    }
}