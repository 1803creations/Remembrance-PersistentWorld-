using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;
using Rage;
using LSPD_First_Response.Engine.Scripting.Entities;
using LSPD_First_Response.Mod.API;
using PersistentWorld.Database;

namespace PersistentWorld.Vehicles
{
    public class VehicleAttachConfig
    {
        public float ScanRadius { get; set; } = 300f;
        public int ScanIntervalMs { get; set; } = 3000;
        public bool EnableLogging { get; set; } = true;
        public bool EnableVehicleModelUpdates { get; set; } = true;
        public bool VerboseDebugging { get; set; } = true;
        public bool IgnoreEmergencyVehicles { get; set; } = true;
        public int MaxUpdatesPerBatch { get; set; } = 5;
        public int UpdateDelayMs { get; set; } = 100;
        public int MaxVehiclesPerScan { get; set; } = 50;
        public int PlateChangeCooldownSeconds { get; set; } = 15;
        public int NameCacheMinutes { get; set; } = 30; // NEW: Cache names for 30 minutes
        public int MaxQueueSize { get; set; } = 100; // NEW: Limit queue size
        public int VehicleProcessCooldownSeconds { get; set; } = 30; // NEW: Don't process same vehicle too often

        public static VehicleAttachConfig Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var serializer = new XmlSerializer(typeof(VehicleAttachConfig));
                    using (var reader = new StreamReader(path))
                    {
                        return (VehicleAttachConfig)serializer.Deserialize(reader);
                    }
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[VehicleAttach] Error loading config: {ex.Message}");
            }
            return new VehicleAttachConfig();
        }

        public void Save(string path)
        {
            try
            {
                var serializer = new XmlSerializer(typeof(VehicleAttachConfig));
                using (var writer = new StreamWriter(path))
                {
                    serializer.Serialize(writer, this);
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[VehicleAttach] Error saving config: {ex.Message}");
            }
        }
    }

    public class VehicleAttach
    {
        private DatabaseManager _database;
        private VehicleAttachConfig _config;
        private string _configPath;
        private DateTime _lastScan = DateTime.MinValue;
        private bool _isEnabled = true;
        private int _processedCount = 0;
        private int _modelUpdatesCompleted = 0;

        // Threading
        private Thread _dbThread;
        private bool _isThreadRunning = false;
        private readonly object _queueLock = new object();
        private AutoResetEvent _updateEvent = new AutoResetEvent(false);
        private bool _disposing = false;

        // Track recently changed plates to avoid rapid re-changes
        private Dictionary<string, DateTime> _recentlyChangedPlates = new Dictionary<string, DateTime>();

        // NEW: Track when vehicles were last processed to avoid processing same vehicle too often
        private Dictionary<int, DateTime> _lastVehicleProcessTime = new Dictionary<int, DateTime>();

        // Queue for vehicle model updates
        private Queue<ModelUpdate> _pendingModelUpdates = new Queue<ModelUpdate>();

        // Track failed updates
        private Dictionary<string, FailedUpdate> _failedUpdateTracker = new Dictionary<string, FailedUpdate>();
        private const int FAILED_UPDATE_COOLDOWN_SECONDS = 30;

        // NEW: Name cache to reduce database lookups
        private Dictionary<string, CachedPerson> _nameCache = new Dictionary<string, CachedPerson>();
        private readonly object _cacheLock = new object();

        // NEW: Track last known model to avoid redundant updates
        private Dictionary<string, string> _lastKnownModel = new Dictionary<string, string>();

        // Debug counters
        private int _totalVehiclesScanned = 0;
        private int _totalNamesFound = 0;
        private int _totalDatabaseMatches = 0;
        private int _totalNameMismatches = 0;
        private int _cacheHits = 0;
        private int _cacheMisses = 0;
        private int _queueFullCount = 0;
        private int _vehicleCooldownSkips = 0;
        private DateTime _lastQueueLog = DateTime.Now;

        private class ModelUpdate
        {
            public string Plate { get; set; }
            public string Model { get; set; }
            public int RetryCount { get; set; }
            public DateTime FirstAttempt { get; set; }
            public DateTime LastAttempt { get; set; }
        }

        private class FailedUpdate
        {
            public string Plate { get; set; }
            public DateTime FailedTime { get; set; }
            public string Reason { get; set; }
        }

        // NEW: Cached person data
        private class CachedPerson
        {
            public Dictionary<string, object> Person { get; set; }
            public string AssignedPlate { get; set; }
            public string FullName { get; set; }
            public DateTime CachedAt { get; set; }
        }

        public VehicleAttach(DatabaseManager database)
        {
            _database = database;
            LoadConfiguration();

            // Start background thread
            _isThreadRunning = true;
            _dbThread = new Thread(DatabaseWorker)
            {
                IsBackground = true,
                Name = "VehicleAttachDBThread",
                Priority = ThreadPriority.BelowNormal
            };
            _dbThread.Start();

            Game.LogTrivial("[VehicleAttach] ========================================");
            Game.LogTrivial("[VehicleAttach] Initialized - DEBUG MODE ENABLED");
            Game.LogTrivial("[VehicleAttach] VerboseDebugging = TRUE");
            Game.LogTrivial("[VehicleAttach] Name caching enabled - 30 minute expiry");
            Game.LogTrivial("[VehicleAttach] ========================================");
        }

        private void LoadConfiguration()
        {
            try
            {
                string gtaPath = AppDomain.CurrentDomain.BaseDirectory;
                _configPath = Path.Combine(gtaPath, "Plugins", "LSPDFR", "PersistentWorld", "VehicleAttachConfig.xml");
                _config = VehicleAttachConfig.Load(_configPath);

                if (!File.Exists(_configPath))
                    _config.Save(_configPath);
            }
            catch
            {
                _config = new VehicleAttachConfig();
            }
        }

        private void Log(string message)
        {
            if (_config.EnableLogging)
                Game.LogTrivial(message);
        }

        private void DebugLog(string message)
        {
            if (_config.VerboseDebugging)
                Game.LogTrivial($"[VehicleAttach] {message}");
        }

        private void DatabaseWorker()
        {
            Game.LogTrivial("[VehicleAttach] Database worker thread started");

            while (_isThreadRunning && !_disposing)
            {
                try
                {
                    bool gotSignal = _updateEvent.WaitOne(1000);

                    if (!_isThreadRunning || _disposing)
                        break;

                    int processedThisCycle = 0;
                    while (processedThisCycle < _config.MaxUpdatesPerBatch)
                    {
                        if (!ProcessSingleModelUpdate())
                            break;
                        processedThisCycle++;
                    }

                    if ((DateTime.Now - _lastQueueLog).TotalSeconds > 30)
                    {
                        lock (_queueLock)
                        {
                            DebugLog($"QUEUE STATUS - Pending:{_pendingModelUpdates.Count} Failed:{_failedUpdateTracker.Count} Completed:{_modelUpdatesCompleted} QueueFull:{_queueFullCount}");
                        }
                        _lastQueueLog = DateTime.Now;
                    }

                    if (DateTime.Now.Second < 5)
                    {
                        CleanupFailedTracker();
                        CleanupCooldownTracker();
                        CleanupVehicleProcessTracker();
                        CleanupNameCache();
                        CleanupLastKnownModel();

                        if (_config.VerboseDebugging)
                        {
                            int cacheSize = 0;
                            lock (_cacheLock)
                            {
                                cacheSize = _nameCache.Count;
                            }
                            DebugLog($"CACHE STATS - Size:{cacheSize} Hits:{_cacheHits} Misses:{_cacheMisses} HitRate:{(int)((float)_cacheHits / (_cacheHits + _cacheMisses + 1) * 100)}%");
                        }
                    }
                }
                catch (ThreadAbortException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DebugLog($"Error in DatabaseWorker: {ex.Message}");
                    Thread.Sleep(1000);
                }
            }

            Game.LogTrivial("[VehicleAttach] Database worker thread stopped");
        }

        private void CleanupFailedTracker()
        {
            try
            {
                var expired = _failedUpdateTracker
                    .Where(kvp => (DateTime.Now - kvp.Value.FailedTime).TotalSeconds > FAILED_UPDATE_COOLDOWN_SECONDS)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var plate in expired)
                {
                    _failedUpdateTracker.Remove(plate);
                    DebugLog($"Removed {plate} from failed tracker after cooldown");
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error in CleanupFailedTracker: {ex.Message}");
            }
        }

        private void CleanupCooldownTracker()
        {
            try
            {
                var expired = _recentlyChangedPlates
                    .Where(kvp => (DateTime.Now - kvp.Value).TotalSeconds > _config.PlateChangeCooldownSeconds)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var plate in expired)
                {
                    _recentlyChangedPlates.Remove(plate);
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error in CleanupCooldownTracker: {ex.Message}");
            }
        }

        private void CleanupVehicleProcessTracker()
        {
            try
            {
                var expired = _lastVehicleProcessTime
                    .Where(kvp => (DateTime.Now - kvp.Value).TotalSeconds > _config.VehicleProcessCooldownSeconds)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var id in expired)
                {
                    _lastVehicleProcessTime.Remove(id);
                }
            }
            catch { }
        }

        private void CleanupNameCache()
        {
            try
            {
                lock (_cacheLock)
                {
                    var expired = _nameCache
                        .Where(kvp => (DateTime.Now - kvp.Value.CachedAt).TotalMinutes > _config.NameCacheMinutes)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in expired)
                    {
                        _nameCache.Remove(key);
                    }

                    if (expired.Count > 0)
                    {
                        DebugLog($"Removed {expired.Count} expired entries from name cache");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error in CleanupNameCache: {ex.Message}");
            }
        }

        private void CleanupLastKnownModel()
        {
            try
            {
                var expired = _lastKnownModel
                    .Where(kvp => !_recentlyChangedPlates.ContainsKey(kvp.Key) && !_pendingModelUpdates.Any(u => u.Plate == kvp.Key))
                    .Select(kvp => kvp.Key)
                    .Take(100)
                    .ToList();

                foreach (var plate in expired)
                {
                    _lastKnownModel.Remove(plate);
                }
            }
            catch { }
        }

        // NEW: Check if model update is actually needed
        private bool DoesModelNeedUpdate(string plate, string currentModel)
        {
            if (_lastKnownModel.TryGetValue(plate, out string lastModel))
            {
                if (lastModel == currentModel)
                {
                    DebugLog($"Skipping {plate} - model already up to date ({currentModel})");
                    return false;
                }
            }
            return true;
        }

        private bool ProcessSingleModelUpdate()
        {
            ModelUpdate update = null;

            lock (_queueLock)
            {
                if (_pendingModelUpdates.Count > 0)
                    update = _pendingModelUpdates.Dequeue();
                else
                    return false;
            }

            if (update == null) return true;

            update.LastAttempt = DateTime.Now;

            if ((DateTime.Now - update.FirstAttempt).TotalMinutes > 5)
            {
                DebugLog($"Dropping stale update for {update.Plate} - in queue too long");
                return true;
            }

            if (_failedUpdateTracker.ContainsKey(update.Plate))
            {
                var failedTime = _failedUpdateTracker[update.Plate].FailedTime;
                var secondsSinceFail = (DateTime.Now - failedTime).TotalSeconds;

                if (secondsSinceFail < FAILED_UPDATE_COOLDOWN_SECONDS)
                {
                    DebugLog($"Skipping {update.Plate} - failed {secondsSinceFail:F0}s ago, requeueing");

                    if (update.RetryCount < 3)
                    {
                        update.RetryCount++;
                        lock (_queueLock)
                        {
                            _pendingModelUpdates.Enqueue(update);
                        }
                        DebugLog($"Requeued {update.Plate} for retry #{update.RetryCount}");
                    }
                    return true;
                }
                else
                {
                    _failedUpdateTracker.Remove(update.Plate);
                }
            }

            Thread.Sleep(_config.UpdateDelayMs);

            try
            {
                DebugLog($"Processing model update for {update.Plate} to {update.Model}");

                _database.UpdateVehicleModel(update.Plate, update.Model);

                _modelUpdatesCompleted++;
                DebugLog($"SUCCESS: Updated model for {update.Plate} to {update.Model}");

                // Update last known model
                lock (_lastKnownModel)
                {
                    _lastKnownModel[update.Plate] = update.Model;
                }

                return true;
            }
            catch (Exception ex)
            {
                DebugLog($"ERROR updating {update.Plate}: {ex.Message}");

                _failedUpdateTracker[update.Plate] = new FailedUpdate
                {
                    Plate = update.Plate,
                    FailedTime = DateTime.Now,
                    Reason = ex.Message
                };

                if (update.RetryCount < 2)
                {
                    update.RetryCount++;
                    DebugLog($"Will retry {update.Plate} later (attempt {update.RetryCount}/3)");

                    Thread.Sleep(1000);
                    lock (_queueLock)
                    {
                        _pendingModelUpdates.Enqueue(update);
                    }
                }
                else
                {
                    DebugLog($"Giving up on {update.Plate} after {update.RetryCount} retries");
                }
                return true;
            }
        }

        public void Update()
        {
            if (!_isEnabled || _disposing) return;

            try
            {
                if ((DateTime.Now - _lastScan).TotalMilliseconds > _config.ScanIntervalMs)
                {
                    ScanNearbyVehicles();
                    _lastScan = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error in Update: {ex.Message}");
            }
        }

        private void ScanNearbyVehicles()
        {
            try
            {
                Vector3 playerPos = Game.LocalPlayer.Character.Position;
                int vehiclesScanned = 0;
                int platesChanged = 0;
                int updatesQueued = 0;

                DebugLog("========== STARTING VEHICLE SCAN ==========");
                DebugLog($"Player position: {playerPos}");

                foreach (var vehicle in World.GetAllVehicles())
                {
                    try
                    {
                        if (!vehicle.Exists()) continue;

                        float distance = Vector3.Distance(vehicle.Position, playerPos);
                        if (distance > _config.ScanRadius) continue;

                        vehiclesScanned++;
                        _totalVehiclesScanned++;

                        // Apply MaxVehiclesPerScan limit
                        if (vehiclesScanned > _config.MaxVehiclesPerScan)
                        {
                            DebugLog($"Reached max vehicles per scan ({_config.MaxVehiclesPerScan})");
                            break;
                        }

                        // NEW: Check if vehicle was processed too recently
                        int vehicleId = vehicle.GetHashCode();
                        if (_lastVehicleProcessTime.TryGetValue(vehicleId, out DateTime lastProcessed))
                        {
                            if ((DateTime.Now - lastProcessed).TotalSeconds < _config.VehicleProcessCooldownSeconds)
                            {
                                _vehicleCooldownSkips++;
                                DebugLog($"Skipping vehicle {vehicle.LicensePlate} - processed {DateTime.Now - lastProcessed:F1}s ago");
                                continue;
                            }
                        }

                        // Skip emergency vehicles if configured
                        if (_config.IgnoreEmergencyVehicles && IsEmergencyVehicle(vehicle))
                        {
                            DebugLog($"Skipping emergency vehicle: {vehicle.Model.Name}");
                            continue;
                        }

                        string currentPlate = vehicle.LicensePlate;
                        string currentPlateClean = CleanPlate(currentPlate);
                        string currentModel = vehicle.Model.Name?.ToLower() ?? "";

                        // Get the driver
                        Ped driver = vehicle.Driver;
                        if (driver == null || !driver.Exists() || !driver.IsHuman)
                        {
                            DebugLog($"Vehicle {currentPlateClean} has no human driver");
                            continue;
                        }

                        // Get driver name
                        string driverName = GetPedNameFromPersona(driver);

                        if (string.IsNullOrEmpty(driverName))
                        {
                            DebugLog($"Could not get name for driver of {currentPlateClean}");
                            continue;
                        }

                        DebugLog($"Driver found: '{driverName}' driving {vehicle.Model.Name} with plate '{currentPlate}'");
                        _totalNamesFound++;

                        // Try to get person from cache first (NEW)
                        var person = GetPersonFromCache(driverName, driver, vehicle);

                        if (person == null)
                        {
                            DebugLog($"No database entry found for '{driverName}'");

                            // Mark vehicle as processed even if no match (so we don't check it again too soon)
                            _lastVehicleProcessTime[vehicleId] = DateTime.Now;
                            continue;
                        }

                        _totalDatabaseMatches++;

                        string assignedPlate = GetAssignedPlateForPerson(person);
                        if (string.IsNullOrEmpty(assignedPlate))
                        {
                            DebugLog($"No assigned plate found for {driverName}");

                            // Mark vehicle as processed
                            _lastVehicleProcessTime[vehicleId] = DateTime.Now;
                            continue;
                        }

                        string assignedPlateClean = CleanPlate(assignedPlate);
                        DebugLog($"Assigned plate from database: '{assignedPlate}' (clean: '{assignedPlateClean}')");

                        // Check if plate needs changing
                        if (currentPlateClean != assignedPlateClean)
                        {
                            DebugLog($"Plate mismatch: Current='{currentPlateClean}', Database='{assignedPlateClean}'");

                            // Check cooldown for this permanent plate
                            if (_recentlyChangedPlates.ContainsKey(assignedPlateClean))
                            {
                                TimeSpan cooldownRemaining = DateTime.Now - _recentlyChangedPlates[assignedPlateClean];
                                DebugLog($"Plate {assignedPlateClean} is in cooldown ({cooldownRemaining.TotalSeconds:F1}s ago)");

                                // Mark vehicle as processed
                                _lastVehicleProcessTime[vehicleId] = DateTime.Now;
                                continue;
                            }

                            // Change the IN-GAME plate
                            vehicle.LicensePlate = assignedPlate;
                            _processedCount++;
                            platesChanged++;

                            _recentlyChangedPlates[assignedPlateClean] = DateTime.Now;

                            Log($"[VehicleAttach] Changed plate for {driverName} from '{currentPlate}' to '{assignedPlate}'");

                            // Queue model update
                            if (QueueModelUpdate(assignedPlateClean, currentModel))
                                updatesQueued++;
                        }
                        else
                        {
                            DebugLog($"Plate already matches database: '{currentPlateClean}'");

                            // Only queue model update if model actually needs updating
                            if (DoesModelNeedUpdate(assignedPlateClean, currentModel))
                            {
                                if (QueueModelUpdate(assignedPlateClean, currentModel))
                                    updatesQueued++;
                            }
                            else
                            {
                                DebugLog($"Model already up to date for {assignedPlateClean}");
                            }
                        }

                        // Mark vehicle as processed
                        _lastVehicleProcessTime[vehicleId] = DateTime.Now;
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"Error processing vehicle: {ex.Message}");
                    }
                }

                DebugLog($"Scan complete: {platesChanged} plates changed, {updatesQueued} updates queued out of {vehiclesScanned} scanned");
                DebugLog($"CACHE STATS - Hits:{_cacheHits} Misses:{_cacheMisses} CooldownSkips:{_vehicleCooldownSkips}");
                DebugLog("========== SCAN END ==========");
            }
            catch (Exception ex)
            {
                Log($"[VehicleAttach] Error in scan: {ex.Message}");
            }
        }

        private bool QueueModelUpdate(string plate, string model)
        {
            if (_config.EnableVehicleModelUpdates &&
                !string.IsNullOrEmpty(model) &&
                model != "unknown")
            {
                lock (_queueLock)
                {
                    // NEW: Check queue size limit
                    if (_pendingModelUpdates.Count >= _config.MaxQueueSize)
                    {
                        _queueFullCount++;
                        DebugLog($"Queue full ({_config.MaxQueueSize}), skipping {plate}");
                        return false;
                    }

                    // Check if already queued
                    var existingUpdate = _pendingModelUpdates.FirstOrDefault(u => u.Plate == plate);

                    if (existingUpdate != null)
                    {
                        // Update the model if different
                        if (existingUpdate.Model != model)
                        {
                            existingUpdate.Model = model;
                            existingUpdate.RetryCount = 0;
                            DebugLog($"Updated queued model for {plate} from {existingUpdate.Model} to {model}");
                        }
                        else
                        {
                            DebugLog($"Model update for {plate} already queued with same model");
                        }
                        return true;
                    }

                    // Check if failed recently
                    if (_failedUpdateTracker.ContainsKey(plate))
                    {
                        var failedTime = _failedUpdateTracker[plate].FailedTime;
                        if ((DateTime.Now - failedTime).TotalSeconds < 30)
                        {
                            DebugLog($"Skipping {plate} - failed recently");
                            return false;
                        }
                        else
                        {
                            _failedUpdateTracker.Remove(plate);
                        }
                    }

                    // Add to queue
                    _pendingModelUpdates.Enqueue(new ModelUpdate
                    {
                        Plate = plate,
                        Model = model,
                        RetryCount = 0,
                        FirstAttempt = DateTime.Now,
                        LastAttempt = DateTime.Now
                    });

                    // Update last known model
                    lock (_lastKnownModel)
                    {
                        _lastKnownModel[plate] = model;
                    }

                    // Signal the worker thread
                    _updateEvent.Set();

                    DebugLog($"Queued model update for plate {plate} to {model} (Queue size: {_pendingModelUpdates.Count})");
                    return true;
                }
            }
            return false;
        }

        private string GenerateRandomSAPlate()
        {
            Random random = new Random();
            int number = random.Next(100000, 999999);
            return $"SA{number}";
        }

        // NEW: Get person from cache or database
        private Dictionary<string, object> GetPersonFromCache(string driverName, Ped driver, Vehicle vehicle)
        {
            string cacheKey = driverName.ToLower().Trim();

            lock (_cacheLock)
            {
                if (_nameCache.TryGetValue(cacheKey, out CachedPerson cached))
                {
                    if ((DateTime.Now - cached.CachedAt).TotalMinutes < _config.NameCacheMinutes)
                    {
                        _cacheHits++;
                        DebugLog($"CACHE HIT: {driverName}");
                        return cached.Person;
                    }
                    else
                    {
                        _nameCache.Remove(cacheKey);
                    }
                }
            }

            _cacheMisses++;
            DebugLog($"CACHE MISS: {driverName} - querying database");

            string[] nameParts = driverName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (nameParts.Length < 1) return null;

            string firstName = nameParts[0];
            string lastName = nameParts.Length > 1 ? string.Join(" ", nameParts.Skip(1)) : "";

            var persons = _database.LookupByName(firstName, lastName);
            if (persons == null || persons.Count == 0) return null;

            var person = FindBestMatchingPerson(persons, driver, vehicle);
            if (person == null) return null;

            string assignedPlate = GetAssignedPlateForPerson(person);
            if (string.IsNullOrEmpty(assignedPlate)) return null;

            lock (_cacheLock)
            {
                _nameCache[cacheKey] = new CachedPerson
                {
                    Person = person,
                    AssignedPlate = assignedPlate,
                    FullName = driverName,
                    CachedAt = DateTime.Now
                };
                DebugLog($"Cached {driverName} - Plate: {assignedPlate}");
            }

            return person;
        }

        private Dictionary<string, object> FindBestMatchingPerson(List<Dictionary<string, object>> persons, Ped driver, Vehicle vehicle)
        {
            if (persons.Count == 1)
            {
                DebugLog("Only one match found, using it");
                return persons[0];
            }

            DebugLog($"Found {persons.Count} matches for driver, attempting to find best match");

            // Try to match by ped model
            string driverModel = driver.Model.Name?.ToLower() ?? "";
            if (!string.IsNullOrEmpty(driverModel))
            {
                foreach (var person in persons)
                {
                    if (person.ContainsKey("model_name"))
                    {
                        string dbModel = person["model_name"].ToString().ToLower();
                        if (!string.IsNullOrEmpty(dbModel) && dbModel == driverModel)
                        {
                            DebugLog($"Matched by ped model: driver={driverModel}, db={dbModel}");
                            return person;
                        }
                    }
                }
            }

            DebugLog($"Could not determine best match, using first");
            return persons[0];
        }

        private string GetAssignedPlateForPerson(Dictionary<string, object> person)
        {
            try
            {
                if (!person.ContainsKey("owned_vehicles"))
                {
                    DebugLog("Person has no owned_vehicles key");
                    return null;
                }

                var vehicles = person["owned_vehicles"] as List<Dictionary<string, object>>;
                if (vehicles == null || vehicles.Count == 0)
                {
                    DebugLog("Person has no vehicles in owned_vehicles list");
                    return null;
                }

                var firstVehicle = vehicles[0];

                if (firstVehicle.ContainsKey("license_plate"))
                {
                    string plate = firstVehicle["license_plate"]?.ToString() ?? "";
                    DebugLog($"First vehicle plate: '{plate}'");
                    return plate;
                }

                return null;
            }
            catch (Exception ex)
            {
                DebugLog($"Error in GetAssignedPlateForPerson: {ex.Message}");
                return null;
            }
        }

        private bool IsEmergencyVehicle(Vehicle vehicle)
        {
            try
            {
                if (vehicle.Class == VehicleClass.Emergency) return true;

                string modelName = vehicle.Model.Name?.ToLower() ?? "";

                if (modelName.Contains("police") ||
                    modelName.Contains("sheriff") ||
                    modelName.Contains("fbi") ||
                    modelName.Contains("fire") ||
                    modelName.Contains("ambulance") ||
                    modelName.Contains("riot") ||
                    modelName.Contains("pranger"))
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private string GetPedNameFromPersona(Ped ped)
        {
            try
            {
                Persona persona = Functions.GetPersonaForPed(ped);

                if (persona != null)
                {
                    string fullName = persona.FullName;
                    if (!string.IsNullOrEmpty(fullName) && fullName != "John Doe" && fullName != "Unknown")
                    {
                        return fullName;
                    }

                    string firstName = persona.Forename;
                    string lastName = persona.Surname;

                    if (!string.IsNullOrEmpty(firstName) && !string.IsNullOrEmpty(lastName) &&
                        firstName != "Unknown" && lastName != "Unknown")
                    {
                        return $"{firstName} {lastName}";
                    }

                    if (!string.IsNullOrEmpty(firstName) && firstName != "Unknown")
                    {
                        return firstName;
                    }
                }

                return "";
            }
            catch (Exception ex)
            {
                DebugLog($"Error in GetPedNameFromPersona: {ex.Message}");
                return "";
            }
        }

        private string CleanPlate(string plate)
        {
            if (string.IsNullOrEmpty(plate))
                return plate;
            return plate.Replace(" ", "").ToUpper().Trim();
        }

        // Public methods
        public void ForceScan()
        {
            Log("[VehicleAttach] Manual scan triggered");
            _recentlyChangedPlates.Clear();
            ScanNearbyVehicles();
        }

        public void ForceProcessUpdates()
        {
            Log("[VehicleAttach] Manual update processing triggered");
            _updateEvent.Set();
        }

        public void Enable()
        {
            _isEnabled = true;
            Log("[VehicleAttach] Enabled");
        }

        public void Disable()
        {
            _isEnabled = false;
            Log("[VehicleAttach] Disabled");
        }

        public void ClearProcessedCache()
        {
            _processedCount = 0;
            _recentlyChangedPlates.Clear();
            _failedUpdateTracker.Clear();
            _lastVehicleProcessTime.Clear();
            _lastKnownModel.Clear();

            lock (_queueLock)
            {
                _pendingModelUpdates.Clear();
            }

            lock (_cacheLock)
            {
                _nameCache.Clear();
            }

            Log("[VehicleAttach] Cache cleared");
        }

        public void ClearUpdateQueue()
        {
            lock (_queueLock)
            {
                _pendingModelUpdates.Clear();
            }
            Log("[VehicleAttach] Update queue cleared");
        }

        public void Dispose()
        {
            _disposing = true;
            _isThreadRunning = false;
            _updateEvent.Set();

            if (_dbThread != null && _dbThread.IsAlive)
            {
                _dbThread.Join(1000);
            }

            _updateEvent?.Dispose();

            lock (_queueLock)
            {
                _pendingModelUpdates.Clear();
            }

            _recentlyChangedPlates.Clear();
            _failedUpdateTracker.Clear();
            _lastVehicleProcessTime.Clear();
            _lastKnownModel.Clear();

            lock (_cacheLock)
            {
                _nameCache.Clear();
            }

            Log("[VehicleAttach] Disposed");
        }

        public int GetProcessedCount() => _processedCount;
        public int GetCompletedModelUpdates() => _modelUpdatesCompleted;
        public int GetQueuedModelUpdates()
        {
            lock (_queueLock)
            {
                return _pendingModelUpdates.Count;
            }
        }
        public int GetFailedUpdateCount() => _failedUpdateTracker.Count;
        public int GetCacheHits() => _cacheHits;
        public int GetCacheMisses() => _cacheMisses;
        public int GetCacheSize()
        {
            lock (_cacheLock)
            {
                return _nameCache.Count;
            }
        }
        public int GetQueueFullCount() => _queueFullCount;
        public int GetVehicleCooldownSkips() => _vehicleCooldownSkips;

        public void SetVerboseDebugging(bool enabled)
        {
            _config.VerboseDebugging = enabled;
            _config.Save(_configPath);
        }

        public void SetModelUpdatesEnabled(bool enabled)
        {
            _config.EnableVehicleModelUpdates = enabled;
            _config.Save(_configPath);
        }

        public void SetIgnoreEmergencyVehicles(bool enabled)
        {
            _config.IgnoreEmergencyVehicles = enabled;
            _config.Save(_configPath);
        }

        public void SetNameCacheMinutes(int minutes)
        {
            _config.NameCacheMinutes = minutes;
            _config.Save(_configPath);
        }

        public void SetMaxQueueSize(int size)
        {
            _config.MaxQueueSize = size;
            _config.Save(_configPath);
        }

        public void SetVehicleProcessCooldown(int seconds)
        {
            _config.VehicleProcessCooldownSeconds = seconds;
            _config.Save(_configPath);
        }
    }
}