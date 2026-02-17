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

namespace PersistentWorld
{
    public class Main : Plugin
    {
        private DatabaseManager _databaseManager;
        private VehicleAttach _vehicleAttach;
        private ComputerScreen _computerScreen;
        private bool _onDuty = false;
        private GameFiber _updateFiber;

        public override void Initialize()
        {
            Game.LogTrivial("======================================");
            Game.LogTrivial("[Persistent World] INITIALIZE STARTED");
            Game.LogTrivial("======================================");

            try
            {
                // Set up database path in GTA V folder
                string gtaPath = AppDomain.CurrentDomain.BaseDirectory;
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
                _computerScreen = new ComputerScreen(_databaseManager);

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

                Game.DisplayNotification("Persistent World Mod ~g~loaded~w~ - Press ~b~F6~w~ or ~b~Hold D-Pad Right~w~ for MDT");
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
                    // F6 to open computer
                    if (Game.IsKeyDown(Keys.F6))
                    {
                        Game.LogTrivial("[Persistent World] F6 pressed - opening computer");
                        Game.DisplayNotification("~g~F6 Pressed - Opening Computer");
                        _computerScreen.Show();
                        GameFiber.Sleep(300);
                    }

                    // F8 to show stats
                    if (Game.IsKeyDown(Keys.F8))
                    {
                        int processedCount = _vehicleAttach.GetProcessedCount();
                        Game.LogTrivial($"[Persistent World] Processed vehicles this session: {processedCount}");
                        Game.DisplayNotification($"~g~Processed Vehicles: {processedCount}");
                        GameFiber.Sleep(300);
                    }

                    // ===== STOP THE PED IMPORT (Shift + F10) =====
                    if (Game.IsKeyDown(Keys.F10) && Game.IsKeyDown(Keys.ShiftKey))
                    {
                        Game.LogTrivial("[Persistent World] ===== MANUAL STP IMPORT TRIGGERED =====");
                        Game.DisplayNotification("~b~Stop The Ped~w~ import started - Check ~y~RPH log~w~ for progress");

                        GameFiber.StartNew(delegate
                        {
                            _databaseManager.ImportStopThePedPeds();
                            Game.DisplayNotification("~g~Import complete!~w~ Check RPH log for details");
                        });

                        GameFiber.Sleep(500);
                    }

                    // ===== MANUAL VEHICLE SCAN (Ctrl + F9) =====
                    if (Game.IsKeyDown(Keys.F9) && Game.IsKeyDown(Keys.ControlKey))
                    {
                        Game.LogTrivial("[Persistent World] ===== MANUAL VEHICLE SCAN TRIGGERED =====");
                        Game.DisplayNotification("~b~Manual vehicle scan started");

                        GameFiber.StartNew(delegate
                        {
                            _vehicleAttach.ForceScan();
                            Game.DisplayNotification("~g~Vehicle scan complete");
                        });

                        GameFiber.Sleep(500);
                    }

                    // ===== CLEAR PROCESSED CACHE (Ctrl + Shift + F9) =====
                    if (Game.IsKeyDown(Keys.F9) && Game.IsKeyDown(Keys.ControlKey) && Game.IsKeyDown(Keys.ShiftKey))
                    {
                        _vehicleAttach.ClearProcessedCache();
                        Game.DisplayNotification("~y~Processed vehicles cache cleared");
                        GameFiber.Sleep(500);
                    }

                    // Controller input
                    bool dpadRight = NativeFunction.Natives.IS_CONTROL_PRESSED<int>(0, 190) == 1;

                    if (dpadRight)
                    {
                        consecutiveMisses = 0;

                        if (dpadPressStart == DateTime.MinValue)
                        {
                            dpadPressStart = DateTime.Now;
                            Game.DisplayNotification("~b~Hold D-Pad Right to open MDT...");
                        }

                        double holdTime = (DateTime.Now - dpadPressStart).TotalSeconds;

                        if (holdTime >= 2.0 && !dpadHeld)
                        {
                            Game.DisplayNotification("~g~Opening MDT...");
                            _computerScreen.Show();
                            dpadHeld = true;
                        }
                        else if (holdTime >= 1.0 && !dpadHeld)
                        {
                            double timeLeft = 2.0 - holdTime;
                            Game.DisplayNotification($"~b~Hold... {timeLeft:F1}s left");
                        }
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
            _databaseManager?.Dispose();
            Game.LogTrivial("[Persistent World] Shutdown complete");
        }

        private void OnDutyStateChanged(bool onDuty)
        {
            Game.LogTrivial($"[Persistent World] OnDutyStateChanged: {onDuty}");
            _onDuty = onDuty;

            if (onDuty)
            {
                Game.DisplayNotification("Persistent World: ~g~On Duty~w~ (Vehicle Sync Only)");

                if (_vehicleAttach != null)
                {
                    _vehicleAttach.Enable();
                    Game.DisplayNotification("~y~Vehicle plate enforcement active");
                }
            }
            else
            {
                Game.DisplayNotification("Persistent World: ~r~Off Duty~w~");
                _vehicleAttach?.Disable();
            }
        }
    }
}