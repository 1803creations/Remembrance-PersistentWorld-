using System;
using System.IO;
using System.Windows.Forms;
using Rage;
using LSPD_First_Response.Mod.API;
using LSPD_First_Response.Engine.Scripting.Entities;
using PersistentWorld.Database;
using PersistentWorld.Computer;
using PersistentWorld.Vehicles;
using Rage.Native;
using System.Xml.Serialization;

namespace PersistentWorld
{
    public class Config
    {
        // Keyboard keys
        public Keys OpenComputerKey { get; set; } = Keys.F6;
        public Keys ShowStatsKey { get; set; } = Keys.F8;
        public Keys ImportStopThePedKey { get; set; } = Keys.F10;
        public Keys ManualScanKey { get; set; } = Keys.F9;
        public Keys ClearCacheKey { get; set; } = Keys.F9;

        // Modifier keys (Ctrl, Shift, Alt)
        public bool ImportStopThePedRequireShift { get; set; } = true;
        public bool ManualScanRequireCtrl { get; set; } = true;
        public bool ClearCacheRequireCtrl { get; set; } = true;
        public bool ClearCacheRequireShift { get; set; } = true;

        // Controller buttons
        public int OpenComputerControllerButton { get; set; } = 190; // D-Pad Right
        public float ControllerHoldTimeSeconds { get; set; } = 2.0f;

        // Computer settings
        public bool EnableComputerSound { get; set; } = true;
        public float ComputerVolume { get; set; } = 0.5f;

        public static Config Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var serializer = new XmlSerializer(typeof(Config));
                    using (var reader = new StreamReader(path))
                    {
                        return (Config)serializer.Deserialize(reader);
                    }
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[Persistent World] Error loading config: {ex.Message}");
            }
            return new Config();
        }

        public void Save(string path)
        {
            try
            {
                var serializer = new XmlSerializer(typeof(Config));
                using (var writer = new StreamWriter(path))
                {
                    serializer.Serialize(writer, this);
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[Persistent World] Error saving config: {ex.Message}");
            }
        }
    }

    public class Main : Plugin
    {
        private DatabaseManager _databaseManager;
        private VehicleAttach _vehicleAttach;
        private ComputerScreen _computerScreen;
        private bool _onDuty = false;
        private GameFiber _updateFiber;
        private Config _config;
        private string _configPath;

        public override void Initialize()
        {
            Game.LogTrivial("======================================");
            Game.LogTrivial("[Persistent World] INITIALIZE STARTED");
            Game.LogTrivial("======================================");

            try
            {
                // Load configuration
                string gtaPath = AppDomain.CurrentDomain.BaseDirectory;
                _configPath = Path.Combine(gtaPath, "Plugins", "LSPDFR", "PersistentWorld", "Config.xml");
                _config = Config.Load(_configPath);

                // Save default config if it doesn't exist
                if (!File.Exists(_configPath))
                {
                    _config.Save(_configPath);
                    Game.LogTrivial("[Persistent World] Created default configuration file");
                }

                Game.LogTrivial($"[Persistent World] Config loaded - Open Computer Key: {_config.OpenComputerKey}");

                // Set up database path in GTA V folder
                Game.LogTrivial($"[Persistent World] GTA Path: {gtaPath}");

                string databasePath = Path.Combine(
                    gtaPath,
                    "Plugins",
                    "LSPDFR",
                    "PersistentWorld",
                    "PersistentWorld.db"
                );

                Game.LogTrivial($"[Persistent World] Database path: {databasePath}");

                // Create directory if it doesn't exist
                Directory.CreateDirectory(Path.GetDirectoryName(databasePath));
                Game.LogTrivial("[Persistent World] Directory created/verified");

                // STEP 1: Create DatabaseManager
                Game.LogTrivial("[Persistent World] Creating DatabaseManager...");
                _databaseManager = new DatabaseManager(databasePath);

                // STEP 2: Initialize database FIRST (open connection, create tables)
                Game.LogTrivial("[Persistent World] Initializing database...");
                _databaseManager.InitializeDatabase();

                // STEP 3: Seed initial data (only if needed)
                Game.LogTrivial("[Persistent World] Seeding initial data...");
                _databaseManager.SeedInitialData();

                // STEP 4: NOW create components that need database access
                Game.LogTrivial("[Persistent World] Creating VehicleAttach...");
                _vehicleAttach = new VehicleAttach(_databaseManager);

                Game.LogTrivial("[Persistent World] Creating ComputerScreen...");
                _computerScreen = new ComputerScreen(_databaseManager, _config);

                // Hook into LSPDFR events
                Game.LogTrivial("[Persistent World] Hooking into OnDutyStateChanged...");
                Functions.OnOnDutyStateChanged += OnDutyStateChanged;

                // Start update fiber for vehicle attach
                Game.LogTrivial("[Persistent World] Starting update fiber...");
                _updateFiber = GameFiber.StartNew(UpdateLoop);

                // Key and controller handler
                Game.LogTrivial("[Persistent World] Starting input handler...");
                GameFiber.StartNew(InputHandler);

                Game.LogTrivial("[Persistent World] ======================================");
                Game.LogTrivial("[Persistent World] INITIALIZE COMPLETED SUCCESSFULLY");
                Game.LogTrivial("[Persistent World] ======================================");

                // ONLY THIS NOTIFICATION REMAINS
                Game.DisplayNotification($"Persistent World Mod ~g~loaded~w~ - Press ~b~{_config.OpenComputerKey}~w~ or ~b~Hold D-Pad Right~w~ for MDT");
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[Persistent World] ERROR DURING INITIALIZATION: {ex.Message}");
                Game.LogTrivial($"[Persistent World] Stack trace: {ex.StackTrace}");
                Game.DisplayNotification("~r~Persistent World Mod failed to load - Check log");
            }
        }

        private void UpdateLoop()
        {
            Game.LogTrivial("[Persistent World] Update fiber started - Vehicle sync only");

            while (true)
            {
                GameFiber.Yield();

                try
                {
                    // Only run vehicle sync - NO PED SPAWNING
                    _vehicleAttach.Update();

                    GameFiber.Sleep(1000);
                }
                catch (Exception ex)
                {
                    Game.LogTrivial($"[Persistent World] Error in update loop: {ex.Message}");
                    GameFiber.Sleep(1000);
                }
            }
        }

        private void InputHandler()
        {
            Game.LogTrivial("[Persistent World] Input handler fiber started");

            DateTime dpadPressStart = DateTime.MinValue;
            bool dpadHeld = false;
            int consecutiveMisses = 0;
            const int MAX_MISSES = 2;

            while (true)
            {
                GameFiber.Yield();

                if (_onDuty)
                {
                    // ===== OPEN COMPUTER =====
                    bool openComputerPressed = Game.IsKeyDown(_config.OpenComputerKey);

                    if (openComputerPressed)
                    {
                        Game.LogTrivial($"[Persistent World] {_config.OpenComputerKey} pressed - opening computer");
                        // REMOVED: Game.DisplayNotification($"~g~{_config.OpenComputerKey} Pressed - Opening Computer");
                        _computerScreen.Show();
                        GameFiber.Sleep(300);
                    }

                    // ===== SHOW STATS =====
                    if (Game.IsKeyDown(_config.ShowStatsKey))
                    {
                        int processedCount = _vehicleAttach.GetProcessedCount();
                        int modelUpdates = _vehicleAttach.GetCompletedModelUpdates();
                        int queuedOps = _vehicleAttach.GetQueuedModelUpdates();
                        int cacheHits = _vehicleAttach.GetCacheHits();
                        int cacheMisses = _vehicleAttach.GetCacheMisses();

                        Game.LogTrivial($"[Persistent World] Stats - Processed: {processedCount}, Models Updated: {modelUpdates}, Queued: {queuedOps}, Cache: {cacheHits}/{cacheMisses}");
                        // REMOVED: Game.DisplayNotification($"~g~Processed: {processedCount}~n~Models: {modelUpdates}~n~Queued: {queuedOps}~n~Cache: {cacheHits}/{cacheMisses}");
                        GameFiber.Sleep(300);
                    }

                    // ===== STOP THE PED IMPORT =====
                    if (Game.IsKeyDown(_config.ImportStopThePedKey))
                    {
                        bool modifiersMatch = true;

                        if (_config.ImportStopThePedRequireShift && !Game.IsKeyDown(Keys.ShiftKey))
                            modifiersMatch = false;

                        if (modifiersMatch)
                        {
                            Game.LogTrivial("[Persistent World] ===== MANUAL STP IMPORT TRIGGERED =====");
                            // REMOVED: Game.DisplayNotification("~b~Stop The Ped~w~ import started - Check ~y~RPH log~w~ for progress");

                            GameFiber.StartNew(delegate
                            {
                                _databaseManager.ImportStopThePedPeds();
                                // REMOVED: Game.DisplayNotification("~g~Import complete!~w~ Check RPH log for details");
                            });

                            GameFiber.Sleep(500);
                        }
                    }

                    // ===== MANUAL VEHICLE SCAN =====
                    if (Game.IsKeyDown(_config.ManualScanKey))
                    {
                        bool modifiersMatch = true;

                        if (_config.ManualScanRequireCtrl && !Game.IsKeyDown(Keys.ControlKey))
                            modifiersMatch = false;

                        if (modifiersMatch)
                        {
                            Game.LogTrivial("[Persistent World] ===== MANUAL VEHICLE SCAN TRIGGERED =====");
                            // REMOVED: Game.DisplayNotification("~b~Manual vehicle scan started");

                            GameFiber.StartNew(delegate
                            {
                                _vehicleAttach.ForceScan();
                                // REMOVED: Game.DisplayNotification("~g~Vehicle scan complete");
                            });

                            GameFiber.Sleep(500);
                        }
                    }

                    // ===== CLEAR PROCESSED CACHE =====
                    if (Game.IsKeyDown(_config.ClearCacheKey))
                    {
                        bool modifiersMatch = true;

                        if (_config.ClearCacheRequireCtrl && !Game.IsKeyDown(Keys.ControlKey))
                            modifiersMatch = false;

                        if (_config.ClearCacheRequireShift && !Game.IsKeyDown(Keys.ShiftKey))
                            modifiersMatch = false;

                        if (modifiersMatch)
                        {
                            _vehicleAttach.ClearProcessedCache();
                            // REMOVED: Game.DisplayNotification("~y~Processed vehicles cache cleared");
                            GameFiber.Sleep(500);
                        }
                    }

                    // Controller input
                    bool dpadRight = NativeFunction.Natives.IS_CONTROL_PRESSED<int>(0, _config.OpenComputerControllerButton) == 1;

                    if (dpadRight)
                    {
                        consecutiveMisses = 0;

                        if (dpadPressStart == DateTime.MinValue)
                        {
                            dpadPressStart = DateTime.Now;
                            // REMOVED: Game.DisplayNotification($"~b~Hold to open MDT ({_config.ControllerHoldTimeSeconds}s)...");
                        }

                        double holdTime = (DateTime.Now - dpadPressStart).TotalSeconds;

                        if (holdTime >= _config.ControllerHoldTimeSeconds && !dpadHeld)
                        {
                            // REMOVED: Game.DisplayNotification("~g~Opening MDT...");
                            _computerScreen.Show();
                            dpadHeld = true;
                        }
                        // REMOVED: else if (holdTime >= 1.0 && !dpadHeld)
                        // {
                        //     double timeLeft = _config.ControllerHoldTimeSeconds - holdTime;
                        //     Game.DisplayNotification($"~b~Hold... {timeLeft:F1}s left");
                        // }
                    }
                    else
                    {
                        if (dpadPressStart != DateTime.MinValue)
                        {
                            consecutiveMisses++;

                            if (consecutiveMisses > MAX_MISSES)
                            {
                                dpadPressStart = DateTime.MinValue;
                                dpadHeld = false;
                                consecutiveMisses = 0;
                            }
                        }
                    }
                }

                GameFiber.Sleep(50);
            }
        }

        public override void Finally()
        {
            Game.LogTrivial("[Persistent World] Mod shutting down...");
            _vehicleAttach?.Disable();
            _vehicleAttach?.Dispose();
            _databaseManager?.Dispose();
            Game.LogTrivial("[Persistent World] Shutdown complete");
        }

        private void OnDutyStateChanged(bool onDuty)
        {
            Game.LogTrivial($"[Persistent World] OnDutyStateChanged: {onDuty}");
            _onDuty = onDuty;

            if (onDuty)
            {
                // REMOVED: Game.DisplayNotification("Persistent World: ~g~On Duty~w~ (Vehicle Sync Only)");

                if (_vehicleAttach != null)
                {
                    _vehicleAttach.Enable();
                    // REMOVED: Game.DisplayNotification("~y~Vehicle plate enforcement active");
                }
            }
            else
            {
                // REMOVED: Game.DisplayNotification("Persistent World: ~r~Off Duty~w~");
                _vehicleAttach?.Disable();
            }
        }
    }
}