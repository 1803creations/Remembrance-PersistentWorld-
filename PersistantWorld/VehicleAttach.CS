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
using LSPD_First_Response;

namespace PersistentWorld.Vehicles
{
    public class VehicleAttachConfig
    {
        public float ScanRadius { get; set; } = 300f;
        public int ScanIntervalMs { get; set; } = 5000;
        public bool EnableLogging { get; set; } = true;
        public bool EnableVehicleModelUpdates { get; set; } = true;
        public bool VerboseDebugging { get; set; } = false;
        public bool IgnoreEmergencyVehicles { get; set; } = true;
        public int MaxUpdatesPerBatch { get; set; } = 5;
        public int UpdateDelayMs { get; set; } = 100;
        public int MaxVehiclesPerScan { get; set; } = 40;
        public int PlateChangeCooldownSeconds { get; set; } = 15;
        public int NameCacheMinutes { get; set; } = 30;
        public int MaxQueueSize { get; set; } = 100;
        public int VehicleProcessCooldownSeconds { get; set; } = 30;
        public int NameProtectionIntervalMs { get; set; } = 3000; // Check every 3 seconds
        public bool AutoGenerateNamesForDuplicates { get; set; } = true;
        public string DefaultFirstName { get; set; } = "John";
        public string DefaultLastName { get; set; } = "Doe";
        public int NameForceAttempts { get; set; } = 3;
        public bool EnableGenderCorrection { get; set; } = true; // NEW: Enable gender correction
        public bool ChangePedModelToMatchGender { get; set; } = true; // NEW: Change ped model to match gender

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
        private DateTime _lastNameProtectionCheck = DateTime.MinValue;
        private bool _isEnabled = true;
        private int _processedCount = 0;
        private int _modelUpdatesCompleted = 0;
        private int _namesProtected = 0;
        private int _duplicateNamesFixed = 0;
        private int _nameForceSuccess = 0;
        private int _nameForceFailures = 0;
        private int _genderCorrections = 0; // NEW: Track gender corrections

        // Threading
        private Thread _dbThread;
        private bool _isThreadRunning = false;
        private readonly object _queueLock = new object();
        private AutoResetEvent _updateEvent = new AutoResetEvent(false);
        private bool _disposing = false;

        // Track vehicles that have already had their plates changed (never change again)
        private HashSet<string> _permanentlyChangedPlates = new HashSet<string>();
        private HashSet<int> _processedVehicleIds = new HashSet<int>();

        // Track recently changed plates to avoid rapid re-changes
        private Dictionary<string, DateTime> _recentlyChangedPlates = new Dictionary<string, DateTime>();

        // Track when vehicles were last processed to avoid processing same vehicle too often
        private Dictionary<int, DateTime> _lastVehicleProcessTime = new Dictionary<int, DateTime>();

        // Queue for vehicle model updates
        private Queue<ModelUpdate> _pendingModelUpdates = new Queue<ModelUpdate>();

        // Track failed updates
        private Dictionary<string, FailedUpdate> _failedUpdateTracker = new Dictionary<string, FailedUpdate>();
        private const int FAILED_UPDATE_COOLDOWN_SECONDS = 30;

        // Name cache to reduce database lookups
        private Dictionary<string, CachedPerson> _nameCache = new Dictionary<string, CachedPerson>();
        private readonly object _cacheLock = new object();

        // Track last known model to avoid redundant updates
        private Dictionary<string, string> _lastKnownModel = new Dictionary<string, string>();

        // Track protected plates
        private Dictionary<string, ProtectedPlate> _protectedPlates = new Dictionary<string, ProtectedPlate>();
        private readonly object _protectedPlatesLock = new object();

        // Track used names to detect duplicates
        private Dictionary<string, Ped> _usedNames = new Dictionary<string, Ped>();
        private readonly object _usedNamesLock = new object();

        // Queue for name protection actions (to be processed on main thread)
        private Queue<NameProtectionAction> _pendingNameActions = new Queue<NameProtectionAction>();
        private readonly object _nameActionLock = new object();

        // Track vehicles and their current drivers
        private Dictionary<Vehicle, string> _vehicleCurrentPlate = new Dictionary<Vehicle, string>();
        private Dictionary<Vehicle, Ped> _vehicleCurrentDriver = new Dictionary<Vehicle, Ped>();
        private readonly object _vehicleLock = new object();

        // Available names pool for generating unique names
        private List<string> _availableFirstNames = new List<string>
        {
            "James", "John", "Robert", "Michael", "William", "David", "Richard", "Joseph", "Thomas", "Charles",
            "Mary", "Patricia", "Jennifer", "Linda", "Elizabeth", "Barbara", "Susan", "Jessica", "Sarah", "Karen",
            "Christopher", "Daniel", "Matthew", "Anthony", "Donald", "Mark", "Paul", "Steven", "Andrew", "Kenneth",
            "Lisa", "Nancy", "Betty", "Helen", "Sandra", "Donna", "Carol", "Ruth", "Sharon", "Michelle",
            "George", "Brian", "Edward", "Ronald", "Timothy", "Jason", "Jeffrey", "Ryan", "Jacob", "Gary",
            "Laura", "Sarah", "Kimberly", "Deborah", "Melissa", "Stephanie", "Rebecca", "Shirley", "Cynthia", "Kathleen",
            "Ashley", "Jessica", "Amanda", "Brittany", "Courtney", "Heather", "Megan", "Nicole", "Rachel", "Danielle" // Added more female names
        };

        private List<string> _availableLastNames = new List<string>
        {
            "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez",
            "Wilson", "Anderson", "Taylor", "Thomas", "Moore", "Jackson", "Martin", "Lee", "Perez", "Thompson",
            "White", "Harris", "Sanchez", "Clark", "Ramirez", "Lewis", "Robinson", "Walker", "Young", "Allen",
            "King", "Wright", "Scott", "Torres", "Nguyen", "Hill", "Flores", "Green", "Adams", "Nelson",
            "Baker", "Hall", "Rivera", "Campbell", "Mitchell", "Carter", "Roberts", "Gomez", "Phillips", "Evans"
        };

        // NEW: Common male and female first names for gender detection
        private HashSet<string> _maleFirstNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "james", "john", "robert", "michael", "william", "david", "richard", "joseph", "thomas", "charles",
            "christopher", "daniel", "matthew", "anthony", "donald", "mark", "paul", "steven", "andrew", "kenneth",
            "george", "brian", "edward", "ronald", "timothy", "jason", "jeffrey", "ryan", "jacob", "gary",
            "nicholas", "eric", "jonathan", "stephen", "larry", "justin", "scott", "brandon", "benjamin", "samuel",
            "gregory", "alexander", "patrick", "frank", "raymond", "jack", "dennis", "jerry", "tyler", "aaron"
        };

        private HashSet<string> _femaleFirstNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mary", "patricia", "jennifer", "linda", "elizabeth", "barbara", "susan", "jessica", "sarah", "karen",
            "lisa", "nancy", "betty", "helen", "sandra", "donna", "carol", "ruth", "sharon", "michelle",
            "laura", "sarah", "kimberly", "deborah", "melissa", "stephanie", "rebecca", "shirley", "cynthia", "kathleen",
            "ashley", "amanda", "brittany", "courtney", "heather", "megan", "nicole", "rachel", "danielle", "christina",
            "amy", "angela", "emily", "katherine", "virginia", "kelly", "lauren", "christine", "tiffany", "denise"
        };

        // NEW: Common male and female ped models
        private Dictionary<string, string> _malePedModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "player_zero", "player_zero" },
            { "player_one", "player_one" },
            { "player_two", "player_two" },
            { "a_m_m_afriamer_01", "a_m_m_afriamer_01" },
            { "a_m_m_beach_01", "a_m_m_beach_01" },
            { "a_m_m_beach_02", "a_m_m_beach_02" },
            { "a_m_m_business_01", "a_m_m_business_01" },
            { "a_m_m_eastsa_01", "a_m_m_eastsa_01" },
            { "a_m_m_eastsa_02", "a_m_m_eastsa_02" },
            { "a_m_m_farmer_01", "a_m_m_farmer_01" },
            { "a_m_m_fatlatin_01", "a_m_m_fatlatin_01" },
            { "a_m_m_genfat_01", "a_m_m_genfat_01" },
            { "a_m_m_genfat_02", "a_m_m_genfat_02" },
            { "a_m_m_golfer_01", "a_m_m_golfer_01" },
            { "a_m_m_hasjew_01", "a_m_m_hasjew_01" },
            { "a_m_m_hillbilly_01", "a_m_m_hillbilly_01" },
            { "a_m_m_hillbilly_02", "a_m_m_hillbilly_02" },
            { "a_m_m_indian_01", "a_m_m_indian_01" },
            { "a_m_m_ktown_01", "a_m_m_ktown_01" },
            { "a_m_m_malibu_01", "a_m_m_malibu_01" },
            { "a_m_m_mexcntry_01", "a_m_m_mexcntry_01" },
            { "a_m_m_mexlabor_01", "a_m_m_mexlabor_01" },
            { "a_m_m_mexthug_01", "a_m_m_mexthug_01" },
            { "a_m_m_og_boss_01", "a_m_m_og_boss_01" },
            { "a_m_m_paparazzi_01", "a_m_m_paparazzi_01" },
            { "a_m_m_polynesian_01", "a_m_m_polynesian_01" },
            { "a_m_m_rurmeth_01", "a_m_m_rurmeth_01" },
            { "a_m_m_salton_01", "a_m_m_salton_01" },
            { "a_m_m_salton_02", "a_m_m_salton_02" },
            { "a_m_m_salton_03", "a_m_m_salton_03" },
            { "a_m_m_salton_04", "a_m_m_salton_04" },
            { "a_m_m_skater_01", "a_m_m_skater_01" },
            { "a_m_m_skater_02", "a_m_m_skater_02" },
            { "a_m_m_socenlat_01", "a_m_m_socenlat_01" },
            { "a_m_m_soucent_01", "a_m_m_soucent_01" },
            { "a_m_m_soucent_02", "a_m_m_soucent_02" },
            { "a_m_m_soucent_03", "a_m_m_soucent_03" },
            { "a_m_m_soucent_04", "a_m_m_soucent_04" },
            { "a_m_m_stlat_01", "a_m_m_stlat_01" },
            { "a_m_m_tennis_01", "a_m_m_tennis_01" },
            { "a_m_m_tourist_01", "a_m_m_tourist_01" },
            { "a_m_m_tramp_01", "a_m_m_tramp_01" },
            { "a_m_m_trampbeac_01", "a_m_m_trampbeac_01" },
            { "a_m_m_tranvest_01", "a_m_m_tranvest_01" },
            { "a_m_o_acult_01", "a_m_o_acult_01" },
            { "a_m_o_beach_01", "a_m_o_beach_01" },
            { "a_m_o_genstreet_01", "a_m_o_genstreet_01" },
            { "a_m_o_ktown_01", "a_m_o_ktown_01" },
            { "a_m_o_salton_01", "a_m_o_salton_01" },
            { "a_m_o_soucent_01", "a_m_o_soucent_01" },
            { "a_m_o_soucent_02", "a_m_o_soucent_02" },
            { "a_m_o_soucent_03", "a_m_o_soucent_03" },
            { "a_m_o_tramp_01", "a_m_o_tramp_01" },
            { "a_m_y_acult_01", "a_m_y_acult_01" },
            { "a_m_y_acult_02", "a_m_y_acult_02" },
            { "a_m_y_beach_01", "a_m_y_beach_01" },
            { "a_m_y_beach_02", "a_m_y_beach_02" },
            { "a_m_y_beach_03", "a_m_y_beach_03" },
            { "a_m_y_beach_04", "a_m_y_beach_04" },
            { "a_m_y_beachvesp_01", "a_m_y_beachvesp_01" },
            { "a_m_y_beachvesp_02", "a_m_y_beachvesp_02" },
            { "a_m_y_bike_01", "a_m_y_bike_01" },
            { "a_m_y_breakdance_01", "a_m_y_breakdance_01" },
            { "a_m_y_busicas_01", "a_m_y_busicas_01" },
            { "a_m_y_business_01", "a_m_y_business_01" },
            { "a_m_y_business_02", "a_m_y_business_02" },
            { "a_m_y_business_03", "a_m_y_business_03" },
            { "a_m_y_cyclist_01", "a_m_y_cyclist_01" },
            { "a_m_y_dhill_01", "a_m_y_dhill_01" },
            { "a_m_y_downtown_01", "a_m_y_downtown_01" },
            { "a_m_y_eastsa_01", "a_m_y_eastsa_01" },
            { "a_m_y_eastsa_02", "a_m_y_eastsa_02" },
            { "a_m_y_epsilon_01", "a_m_y_epsilon_01" },
            { "a_m_y_epsilon_02", "a_m_y_epsilon_02" },
            { "a_m_y_gay_01", "a_m_y_gay_01" },
            { "a_m_y_gay_02", "a_m_y_gay_02" },
            { "a_m_y_genstreet_01", "a_m_y_genstreet_01" },
            { "a_m_y_genstreet_02", "a_m_y_genstreet_02" },
            { "a_m_y_golfer_01", "a_m_y_golfer_01" },
            { "a_m_y_hasjew_01", "a_m_y_hasjew_01" },
            { "a_m_y_hiker_01", "a_m_y_hiker_01" },
            { "a_m_y_hippy_01", "a_m_y_hippy_01" },
            { "a_m_y_hipster_01", "a_m_y_hipster_01" },
            { "a_m_y_hipster_02", "a_m_y_hipster_02" },
            { "a_m_y_hipster_03", "a_m_y_hipster_03" },
            { "a_m_y_indian_01", "a_m_y_indian_01" },
            { "a_m_y_jetski_01", "a_m_y_jetski_01" },
            { "a_m_y_juggalo_01", "a_m_y_juggalo_01" },
            { "a_m_y_ktown_01", "a_m_y_ktown_01" },
            { "a_m_y_ktown_02", "a_m_y_ktown_02" },
            { "a_m_y_latino_01", "a_m_y_latino_01" },
            { "a_m_y_methhead_01", "a_m_y_methhead_01" },
            { "a_m_y_mexthug_01", "a_m_y_mexthug_01" },
            { "a_m_y_motox_01", "a_m_y_motox_01" },
            { "a_m_y_motox_02", "a_m_y_motox_02" },
            { "a_m_y_musclbeac_01", "a_m_y_musclbeac_01" },
            { "a_m_y_musclbeac_02", "a_m_y_musclbeac_02" },
            { "a_m_y_og_boss_01", "a_m_y_og_boss_01" },
            { "a_m_y_paparazzi_01", "a_m_y_paparazzi_01" },
            { "a_m_y_psilon_01", "a_m_y_psilon_01" },
            { "a_m_y_racy_01", "a_m_y_racy_01" },
            { "a_m_y_skater_01", "a_m_y_skater_01" },
            { "a_m_y_skater_02", "a_m_y_skater_02" },
            { "a_m_y_soucent_01", "a_m_y_soucent_01" },
            { "a_m_y_soucent_02", "a_m_y_soucent_02" },
            { "a_m_y_soucent_03", "a_m_y_soucent_03" },
            { "a_m_y_soucent_04", "a_m_y_soucent_04" },
            { "a_m_y_stbla_01", "a_m_y_stbla_01" },
            { "a_m_y_stbla_02", "a_m_y_stbla_02" },
            { "a_m_y_stlat_01", "a_m_y_stlat_01" },
            { "a_m_y_stwhi_01", "a_m_y_stwhi_01" },
            { "a_m_y_stwhi_02", "a_m_y_stwhi_02" },
            { "a_m_y_sunbathe_01", "a_m_y_sunbathe_01" },
            { "a_m_y_surfer_01", "a_m_y_surfer_01" },
            { "a_m_y_tattoo_01", "a_m_y_tattoo_01" },
            { "a_m_y_tennis_01", "a_m_y_tennis_01" },
            { "a_m_y_tourist_01", "a_m_y_tourist_01" },
            { "a_m_y_tourist_02", "a_m_y_tourist_02" },
            { "a_m_y_tranvest_01", "a_m_y_tranvest_01" },
            { "a_m_y_tranvest_02", "a_m_y_tranvest_02" },
            { "a_m_y_valley_01", "a_m_y_valley_01" },
            { "a_m_y_valley_02", "a_m_y_valley_02" },
            { "a_m_y_vindouche_01", "a_m_y_vindouche_01" },
            { "a_m_y_vinewood_01", "a_m_y_vinewood_01" },
            { "a_m_y_vinewood_02", "a_m_y_vinewood_02" },
            { "a_m_y_vinewood_03", "a_m_y_vinewood_03" },
            { "a_m_y_vinewood_04", "a_m_y_vinewood_04" },
            { "a_m_y_yoga_01", "a_m_y_yoga_01" }
        };

        private Dictionary<string, string> _femalePedModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "player_zero", "player_zero" },
            { "player_one", "player_one" },
            { "player_two", "player_two" },
            { "a_f_m_beach_01", "a_f_m_beach_01" },
            { "a_f_m_bevhills_01", "a_f_m_bevhills_01" },
            { "a_f_m_bevhills_02", "a_f_m_bevhills_02" },
            { "a_f_m_bodybuild_01", "a_f_m_bodybuild_01" },
            { "a_f_m_business_01", "a_f_m_business_01" },
            { "a_f_m_business_02", "a_f_m_business_02" },
            { "a_f_m_downtown_01", "a_f_m_downtown_01" },
            { "a_f_m_eastsa_01", "a_f_m_eastsa_01" },
            { "a_f_m_eastsa_02", "a_f_m_eastsa_02" },
            { "a_f_m_fatbla_01", "a_f_m_fatbla_01" },
            { "a_f_m_fatcult_01", "a_f_m_fatcult_01" },
            { "a_f_m_fatwhite_01", "a_f_m_fatwhite_01" },
            { "a_f_m_ktown_01", "a_f_m_ktown_01" },
            { "a_f_m_ktown_02", "a_f_m_ktown_02" },
            { "a_f_m_prolhost_01", "a_f_m_prolhost_01" },
            { "a_f_m_salton_01", "a_f_m_salton_01" },
            { "a_f_m_skidrow_01", "a_f_m_skidrow_01" },
            { "a_f_m_soucent_01", "a_f_m_soucent_01" },
            { "a_f_m_soucent_02", "a_f_m_soucent_02" },
            { "a_f_m_soucent_03", "a_f_m_soucent_03" },
            { "a_f_m_tourist_01", "a_f_m_tourist_01" },
            { "a_f_m_tramp_01", "a_f_m_tramp_01" },
            { "a_f_m_trampbeac_01", "a_f_m_trampbeac_01" },
            { "a_f_o_genstreet_01", "a_f_o_genstreet_01" },
            { "a_f_o_indian_01", "a_f_o_indian_01" },
            { "a_f_o_ktown_01", "a_f_o_ktown_01" },
            { "a_f_o_salton_01", "a_f_o_salton_01" },
            { "a_f_o_soucent_01", "a_f_o_soucent_01" },
            { "a_f_o_soucent_02", "a_f_o_soucent_02" },
            { "a_f_y_beach_01", "a_f_y_beach_01" },
            { "a_f_y_bevhills_01", "a_f_y_bevhills_01" },
            { "a_f_y_bevhills_02", "a_f_y_bevhills_02" },
            { "a_f_y_bevhills_03", "a_f_y_bevhills_03" },
            { "a_f_y_bevhills_04", "a_f_y_bevhills_04" },
            { "a_f_y_business_01", "a_f_y_business_01" },
            { "a_f_y_business_02", "a_f_y_business_02" },
            { "a_f_y_business_03", "a_f_y_business_03" },
            { "a_f_y_business_04", "a_f_y_business_04" },
            { "a_f_y_eastsa_01", "a_f_y_eastsa_01" },
            { "a_f_y_eastsa_02", "a_f_y_eastsa_02" },
            { "a_f_y_eastsa_03", "a_f_y_eastsa_03" },
            { "a_f_y_epsilon_01", "a_f_y_epsilon_01" },
            { "a_f_y_fitness_01", "a_f_y_fitness_01" },
            { "a_f_y_fitness_02", "a_f_y_fitness_02" },
            { "a_f_y_golfer_01", "a_f_y_golfer_01" },
            { "a_f_y_hiker_01", "a_f_y_hiker_01" },
            { "a_f_y_hipster_01", "a_f_y_hipster_01" },
            { "a_f_y_hipster_02", "a_f_y_hipster_02" },
            { "a_f_y_hipster_03", "a_f_y_hipster_03" },
            { "a_f_y_hipster_04", "a_f_y_hipster_04" },
            { "a_f_y_indian_01", "a_f_y_indian_01" },
            { "a_f_y_jetski_01", "a_f_y_jetski_01" },
            { "a_f_y_juggalo_01", "a_f_y_juggalo_01" },
            { "a_f_y_ktown_01", "a_f_y_ktown_01" },
            { "a_f_y_ktown_02", "a_f_y_ktown_02" },
            { "a_f_y_latino_01", "a_f_y_latino_01" },
            { "a_f_y_methhead_01", "a_f_y_methhead_01" },
            { "a_f_y_mexthug_01", "a_f_y_mexthug_01" },
            { "a_f_y_motox_01", "a_f_y_motox_01" },
            { "a_f_y_motox_02", "a_f_y_motox_02" },
            { "a_f_y_musclbeac_01", "a_f_y_musclbeac_01" },
            { "a_f_y_musclbeac_02", "a_f_y_musclbeac_02" },
            { "a_f_y_og_boss_01", "a_f_y_og_boss_01" },
            { "a_f_y_psilon_01", "a_f_y_psilon_01" },
            { "a_f_y_racy_01", "a_f_y_racy_01" },
            { "a_f_y_skater_01", "a_f_y_skater_01" },
            { "a_f_y_soucent_01", "a_f_y_soucent_01" },
            { "a_f_y_soucent_02", "a_f_y_soucent_02" },
            { "a_f_y_soucent_03", "a_f_y_soucent_03" },
            { "a_f_y_tennis_01", "a_f_y_tennis_01" },
            { "a_f_y_tourist_01", "a_f_y_tourist_01" },
            { "a_f_y_tourist_02", "a_f_y_tourist_02" },
            { "a_f_y_valley_01", "a_f_y_valley_01" },
            { "a_f_y_valley_02", "a_f_y_valley_02" },
            { "a_f_y_vindouche_01", "a_f_y_vindouche_01" },
            { "a_f_y_vinewood_01", "a_f_y_vinewood_01" },
            { "a_f_y_vinewood_02", "a_f_y_vinewood_02" },
            { "a_f_y_vinewood_03", "a_f_y_vinewood_03" },
            { "a_f_y_vinewood_04", "a_f_y_vinewood_04" },
            { "a_f_y_yoga_01", "a_f_y_yoga_01" }
        };

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

        // Protected plate data
        private class ProtectedPlate
        {
            public string Plate { get; set; }
            public string ExpectedFirstName { get; set; }
            public string ExpectedLastName { get; set; }
            public string ExpectedFullName { get; set; }
            public DateTime ProtectedSince { get; set; }
            public string OriginalPlate { get; set; }
            public int ForceCount { get; set; }
            public DateTime LastForceTime { get; set; }
            public Ped CurrentDriver { get; set; }
            public Vehicle CurrentVehicle { get; set; }
            public string ExpectedGender { get; set; } // NEW: Store expected gender as string
        }

        // Cached person data
        private class CachedPerson
        {
            public Dictionary<string, object> Person { get; set; }
            public string AssignedPlate { get; set; }
            public string FullName { get; set; }
            public DateTime CachedAt { get; set; }
            public string DetectedGender { get; set; } // NEW: Store detected gender as string
        }

        // Name protection action to be processed on main thread
        private class NameProtectionAction
        {
            public Ped TargetPed { get; set; }
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public string FullName { get; set; }
            public string Plate { get; set; }
            public string OriginalPlate { get; set; }
            public int AttemptCount { get; set; }
            public ActionType Action { get; set; }
            public string TargetGender { get; set; } // NEW: Target gender for this name as string

            public enum ActionType
            {
                ForceName,
                VerifyName,
                CheckNewDriver
            }
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
            Game.LogTrivial("[VehicleAttach] PLATE PROTECTION ENABLED - Protecting plates, not peds");
            Game.LogTrivial("[VehicleAttach] GENDER CORRECTION ENABLED - Will fix gender mismatches"); // NEW
            Game.LogTrivial("[VehicleAttach] DUPLICATE DETECTION ENABLED - Will fix duplicate names");
            Game.LogTrivial("[VehicleAttach] PERMANENT PLATE TRACKING - Plates only changed once");
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
                        CleanupProtectedPlates();
                        CleanupUsedNames();
                        CleanupVehicleTracking();

                        if (_config.VerboseDebugging)
                        {
                            int cacheSize = 0;
                            lock (_cacheLock)
                            {
                                cacheSize = _nameCache.Count;
                            }
                            int protectedCount = 0;
                            lock (_protectedPlatesLock)
                            {
                                protectedCount = _protectedPlates.Count;
                            }
                            int usedNamesCount = 0;
                            lock (_usedNamesLock)
                            {
                                usedNamesCount = _usedNames.Count;
                            }
                            int vehicleCount = 0;
                            lock (_vehicleLock)
                            {
                                vehicleCount = _vehicleCurrentDriver.Count;
                            }
                            DebugLog($"CACHE STATS - Size:{cacheSize} Hits:{_cacheHits} Misses:{_cacheMisses} HitRate:{(int)((float)_cacheHits / (_cacheHits + _cacheMisses + 1) * 100)}%");
                            DebugLog($"PROTECTION STATS - Protected Plates:{protectedCount} NamesProtected:{_namesProtected} ForceSuccess:{_nameForceSuccess} ForceFail:{_nameForceFailures} GenderCorrections:{_genderCorrections}");
                            DebugLog($"DUPLICATE STATS - UsedNames:{usedNamesCount} DuplicatesFixed:{_duplicateNamesFixed}");
                            DebugLog($"PLATE STATS - PermanentlyChanged:{_permanentlyChangedPlates.Count} ProcessedVehicles:{_processedVehicleIds.Count} TrackedVehicles:{vehicleCount}");
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

        // Process name protection actions on the main thread (called from Update)
        private void ProcessNameActions()
        {
            try
            {
                List<NameProtectionAction> actions = new List<NameProtectionAction>();

                lock (_nameActionLock)
                {
                    while (_pendingNameActions.Count > 0)
                    {
                        actions.Add(_pendingNameActions.Dequeue());
                    }
                }

                foreach (var action in actions)
                {
                    try
                    {
                        if (!action.TargetPed.Exists()) continue;

                        switch (action.Action)
                        {
                            case NameProtectionAction.ActionType.ForceName:
                                bool success = ForcePedNameInternal(action.TargetPed, action.FirstName, action.LastName, action.TargetGender);
                                if (success)
                                {
                                    _nameForceSuccess++;
                                    DebugLog($"Successfully forced name for ped: {action.FirstName} {action.LastName} (Plate: {action.Plate})");
                                }
                                else
                                {
                                    _nameForceFailures++;
                                    DebugLog($"Failed to force name for ped: {action.FirstName} {action.LastName} (Plate: {action.Plate})");
                                }
                                break;

                            case NameProtectionAction.ActionType.VerifyName:
                                try
                                {
                                    Persona persona = Functions.GetPersonaForPed(action.TargetPed);
                                    if (persona != null)
                                    {
                                        string currentName = persona.FullName;
                                        if (currentName != action.FullName)
                                        {
                                            DebugLog($"Verification failed for plate {action.Plate} - current: {currentName}, expected: {action.FullName}");
                                            // Queue another force attempt
                                            for (int i = 0; i < _config.NameForceAttempts; i++)
                                            {
                                                QueueNameForce(action.TargetPed, action.FirstName, action.LastName,
                                                    action.FullName, action.Plate, action.OriginalPlate, action.TargetGender);
                                            }
                                        }
                                        else
                                        {
                                            DebugLog($"Verification successful for plate {action.Plate} - name is correct: {currentName}");
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    DebugLog($"Error in verification: {ex.Message}");
                                }
                                break;

                            case NameProtectionAction.ActionType.CheckNewDriver:
                                // This action is for when a new driver enters a protected vehicle
                                DebugLog($"New driver detected for protected plate {action.Plate} - forcing name to {action.FirstName} {action.LastName}");
                                for (int i = 0; i < _config.NameForceAttempts; i++)
                                {
                                    QueueNameForce(action.TargetPed, action.FirstName, action.LastName,
                                        action.FullName, action.Plate, action.OriginalPlate, action.TargetGender);
                                }
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"Error processing name action: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error in ProcessNameActions: {ex.Message}");
            }
        }

        // Queue a name force action (to be processed on main thread)
        private void QueueNameForce(Ped ped, string firstName, string lastName, string fullName,
            string plate, string originalPlate, string targetGender)
        {
            lock (_nameActionLock)
            {
                _pendingNameActions.Enqueue(new NameProtectionAction
                {
                    TargetPed = ped,
                    FirstName = firstName,
                    LastName = lastName,
                    FullName = fullName,
                    Plate = plate,
                    OriginalPlate = originalPlate,
                    AttemptCount = 1,
                    Action = NameProtectionAction.ActionType.ForceName,
                    TargetGender = targetGender
                });
            }
        }

        // Queue a name verification action
        private void QueueNameVerification(Ped ped, string firstName, string lastName, string fullName,
            string plate, string originalPlate, string targetGender)
        {
            lock (_nameActionLock)
            {
                _pendingNameActions.Enqueue(new NameProtectionAction
                {
                    TargetPed = ped,
                    FirstName = firstName,
                    LastName = lastName,
                    FullName = fullName,
                    Plate = plate,
                    OriginalPlate = originalPlate,
                    Action = NameProtectionAction.ActionType.VerifyName,
                    TargetGender = targetGender
                });
            }
        }

        // Queue a new driver check action
        private void QueueNewDriverCheck(Ped ped, string firstName, string lastName, string fullName,
            string plate, string originalPlate, string targetGender)
        {
            lock (_nameActionLock)
            {
                _pendingNameActions.Enqueue(new NameProtectionAction
                {
                    TargetPed = ped,
                    FirstName = firstName,
                    LastName = lastName,
                    FullName = fullName,
                    Plate = plate,
                    OriginalPlate = originalPlate,
                    Action = NameProtectionAction.ActionType.CheckNewDriver,
                    TargetGender = targetGender
                });
            }
        }

        // NEW: Detect gender from first name (returns string "Male", "Female", or "Unknown")
        private string DetectGenderFromName(string firstName)
        {
            if (string.IsNullOrEmpty(firstName))
                return "Unknown";

            string lowerFirstName = firstName.ToLower();

            if (_maleFirstNames.Contains(lowerFirstName))
                return "Male";

            if (_femaleFirstNames.Contains(lowerFirstName))
                return "Female";

            // Try to guess by last character (common pattern)
            if (lowerFirstName.EndsWith("a") || lowerFirstName.EndsWith("e") ||
                lowerFirstName.EndsWith("y") || lowerFirstName == "ashley")
                return "Female";

            return "Unknown";
        }

        // NEW: Get a random ped model for the specified gender
        private string GetRandomPedModelForGender(string gender)
        {
            Random random = new Random();

            if (gender == "Male" && _malePedModels.Count > 0)
            {
                return _malePedModels.Values.ElementAt(random.Next(_malePedModels.Count));
            }
            else if (gender == "Female" && _femalePedModels.Count > 0)
            {
                return _femalePedModels.Values.ElementAt(random.Next(_femalePedModels.Count));
            }

            // Default fallbacks
            return gender == "Male" ? "a_m_m_beach_01" : "a_f_m_beach_01";
        }

        // NEW: Change ped model to match gender
        private bool ChangePedModel(Ped ped, string targetGender)
        {
            try
            {
                if (!_config.ChangePedModelToMatchGender)
                {
                    DebugLog("Ped model changing is disabled in config");
                    return false;
                }

                string targetModelName = GetRandomPedModelForGender(targetGender);

                DebugLog($"Attempting to change ped model to {targetModelName} for gender {targetGender}");

                // Load the model
                Model targetModel = new Model(targetModelName);
                if (!targetModel.IsValid)
                {
                    DebugLog($"Invalid model: {targetModelName}");
                    return false;
                }

                // Wait for model to load
                targetModel.LoadAndWait();

                // Change the ped's model
                ped.Model = targetModel;

             

                DebugLog($"Successfully changed ped model to {targetModelName}");
                _genderCorrections++;
                return true;
            }
            catch (Exception ex)
            {
                DebugLog($"Error changing ped model: {ex.Message}");
                return false;
            }
        }

        // Modified internal method to force a ped's name (called on main thread)
        private bool ForcePedNameInternal(Ped ped, string firstName, string lastName, string targetGender)
        {
            try
            {
                DebugLog($"Attempting to force name for ped to: {firstName} {lastName} (Target Gender: {targetGender})");

                // Get the existing persona
                Persona currentPersona = Functions.GetPersonaForPed(ped);

                if (currentPersona != null)
                {
                    // Check for gender mismatch if enabled
                    if (_config.EnableGenderCorrection && targetGender != "Unknown")
                    {
                        string currentGender = currentPersona.Gender.ToString();

                        if (currentGender != targetGender)
                        {
                            DebugLog($"GENDER MISMATCH DETECTED! Current: {currentGender}, Target: {targetGender}");

                            // Change the ped model first if enabled
                            if (_config.ChangePedModelToMatchGender)
                            {
                                ChangePedModel(ped, targetGender);
                            }

                            // FIXED: Parse gender string to Gender enum
                            Gender newGender = targetGender == "Male" ? Gender.Male : Gender.Female;

                            // Create new persona with correct gender
                            Persona newPersona = new Persona(firstName, lastName, newGender);
                            Functions.SetPersonaForPed(ped, newPersona);
                        }
                        else
                        {
                            // Gender matches, just update name
                            Persona newPersona = new Persona(firstName, lastName, currentPersona.Gender);
                            Functions.SetPersonaForPed(ped, newPersona);
                        }
                    }
                    else
                    {
                        // No gender correction, preserve current gender
                        Persona newPersona = new Persona(firstName, lastName, currentPersona.Gender);
                        Functions.SetPersonaForPed(ped, newPersona);
                    }

                    // Get the persona again to verify
                    Persona afterPersona = Functions.GetPersonaForPed(ped);
                    if (afterPersona != null)
                    {
                        DebugLog($"Name after force: {afterPersona.FullName} (Gender: {afterPersona.Gender})");

                        if (afterPersona.FullName == $"{firstName} {lastName}")
                        {
                            DebugLog($"Name force SUCCESSFUL - name is now correct");
                            return true;
                        }
                        else
                        {
                            DebugLog($"Name force FAILED - name is still {afterPersona.FullName}");
                            return false;
                        }
                    }

                    return true;
                }
                else
                {
                    DebugLog($"No existing persona found for ped");
                    return false;
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error in ForcePedNameInternal: {ex.Message}");
                return false;
            }
        }

        // Check for driver changes in vehicles
        private void CheckForDriverChanges()
        {
            try
            {
                List<Vehicle> vehiclesToRemove = new List<Vehicle>();

                lock (_vehicleLock)
                {
                    foreach (var kvp in _vehicleCurrentDriver.ToList())
                    {
                        Vehicle vehicle = kvp.Key;
                        Ped lastDriver = kvp.Value;

                        if (!vehicle.Exists())
                        {
                            vehiclesToRemove.Add(vehicle);
                            continue;
                        }

                        Ped currentDriver = vehicle.Driver;

                        // If driver changed
                        if (currentDriver != lastDriver)
                        {
                            string plate = CleanPlate(vehicle.LicensePlate);

                            // Update tracking
                            _vehicleCurrentDriver[vehicle] = currentDriver;

                            // Check if this plate is protected
                            lock (_protectedPlatesLock)
                            {
                                if (_protectedPlates.ContainsKey(plate))
                                {
                                    ProtectedPlate protectedData = _protectedPlates[plate];

                                    if (currentDriver != null && currentDriver.Exists())
                                    {
                                        DebugLog($"DRIVER CHANGE DETECTED for protected plate {plate}");
                                        DebugLog($"  - New driver: {currentDriver.Handle}");
                                        DebugLog($"  - Expected name: {protectedData.ExpectedFullName}");
                                        DebugLog($"  - Expected gender: {protectedData.ExpectedGender}");

                                        // Update the protected plate with new driver
                                        protectedData.CurrentDriver = currentDriver;
                                        protectedData.CurrentVehicle = vehicle;

                                        // Queue name force for the new driver
                                        QueueNewDriverCheck(currentDriver,
                                            protectedData.ExpectedFirstName,
                                            protectedData.ExpectedLastName,
                                            protectedData.ExpectedFullName,
                                            plate, protectedData.OriginalPlate,
                                            protectedData.ExpectedGender);
                                    }
                                }
                            }
                        }
                    }

                    // Remove dead vehicles
                    foreach (var vehicle in vehiclesToRemove)
                    {
                        _vehicleCurrentDriver.Remove(vehicle);
                        _vehicleCurrentPlate.Remove(vehicle);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error in CheckForDriverChanges: {ex.Message}");
            }
        }

        // Modified: Check protected plates and ensure drivers have correct names and genders
        private void CheckProtectedPlates()
        {
            try
            {
                List<string> platesToRemove = new List<string>();

                lock (_protectedPlatesLock)
                {
                    foreach (var kvp in _protectedPlates.ToList())
                    {
                        string plate = kvp.Key;
                        ProtectedPlate protectedData = kvp.Value;

                        // Check if vehicle still exists
                        if (protectedData.CurrentVehicle == null || !protectedData.CurrentVehicle.Exists())
                        {
                            // Try to find the vehicle by plate
                            Vehicle foundVehicle = null;
                            foreach (var vehicle in World.GetAllVehicles())
                            {
                                if (vehicle.Exists() && CleanPlate(vehicle.LicensePlate) == plate)
                                {
                                    foundVehicle = vehicle;
                                    break;
                                }
                            }

                            if (foundVehicle != null && foundVehicle.Exists())
                            {
                                protectedData.CurrentVehicle = foundVehicle;
                                protectedData.CurrentDriver = foundVehicle.Driver;
                                DebugLog($"Reconnected to vehicle {plate}");
                            }
                            else
                            {
                                // Vehicle no longer exists in world
                                platesToRemove.Add(plate);
                                DebugLog($"Removing protection for plate {plate} - vehicle no longer exists");
                                continue;
                            }
                        }

                        // Check current driver
                        Ped currentDriver = protectedData.CurrentVehicle.Driver;

                        if (currentDriver != null && currentDriver.Exists())
                        {
                            // Update stored driver if changed
                            if (currentDriver != protectedData.CurrentDriver)
                            {
                                protectedData.CurrentDriver = currentDriver;
                                DebugLog($"Driver changed for protected plate {plate} - new driver detected");
                            }

                            // Get current name and gender
                            Persona persona = Functions.GetPersonaForPed(currentDriver);
                            if (persona != null)
                            {
                                string currentName = persona.FullName;
                                string expectedName = protectedData.ExpectedFullName;
                                string currentGender = persona.Gender.ToString();
                                string expectedGender = protectedData.ExpectedGender;

                                bool needsCorrection = false;

                                // Check name mismatch
                                if (currentName != expectedName)
                                {
                                    DebugLog($"NAME MISMATCH for protected plate {plate}!");
                                    DebugLog($"  - Expected: {expectedName}");
                                    DebugLog($"  - Current: {currentName}");
                                    needsCorrection = true;
                                }

                                // Check gender mismatch if enabled
                                if (_config.EnableGenderCorrection && expectedGender != "Unknown" && currentGender != expectedGender)
                                {
                                    DebugLog($"GENDER MISMATCH for protected plate {plate}!");
                                    DebugLog($"  - Expected gender: {expectedGender}");
                                    DebugLog($"  - Current gender: {currentGender}");
                                    needsCorrection = true;
                                }

                                if (needsCorrection)
                                {
                                    DebugLog($"  - Forcing correction...");

                                    // Queue force actions
                                    for (int i = 0; i < _config.NameForceAttempts; i++)
                                    {
                                        QueueNameForce(currentDriver,
                                            protectedData.ExpectedFirstName,
                                            protectedData.ExpectedLastName,
                                            expectedName, plate, protectedData.OriginalPlate,
                                            expectedGender);
                                    }

                                    // Queue verification
                                    QueueNameVerification(currentDriver,
                                        protectedData.ExpectedFirstName,
                                        protectedData.ExpectedLastName,
                                        expectedName, plate, protectedData.OriginalPlate,
                                        expectedGender);

                                    protectedData.ForceCount++;
                                    protectedData.LastForceTime = DateTime.Now;
                                    _namesProtected++;
                                }
                            }
                        }
                    }

                    // Remove dead plates
                    foreach (var plate in platesToRemove)
                    {
                        _protectedPlates.Remove(plate);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error in CheckProtectedPlates: {ex.Message}");
            }
        }

        // Modified debug method to log all protected plates with gender info
        private void DebugLogProtectedPlates()
        {
            try
            {
                if (!_config.VerboseDebugging) return;

                DebugLog("========== PROTECTED PLATES STATUS ==========");

                lock (_protectedPlatesLock)
                {
                    foreach (var kvp in _protectedPlates)
                    {
                        string plate = kvp.Key;
                        ProtectedPlate protectedData = kvp.Value;

                        string vehicleStatus = protectedData.CurrentVehicle?.Exists() == true ? "Exists" : "Missing";
                        string driverStatus = protectedData.CurrentDriver?.Exists() == true ? "Exists" : "Missing";
                        string currentName = "Unknown";
                        string currentGender = "Unknown";

                        if (protectedData.CurrentDriver?.Exists() == true)
                        {
                            Persona persona = Functions.GetPersonaForPed(protectedData.CurrentDriver);
                            currentName = persona?.FullName ?? "Unknown";
                            currentGender = persona?.Gender.ToString() ?? "Unknown";
                        }

                        DebugLog($"PLATE: {plate}");
                        DebugLog($"  - Expected Name: {protectedData.ExpectedFullName}");
                        DebugLog($"  - Expected Gender: {protectedData.ExpectedGender}");
                        DebugLog($"  - Current Name: {currentName}");
                        DebugLog($"  - Current Gender: {currentGender}");
                        DebugLog($"  - Original Plate: {protectedData.OriginalPlate}");
                        DebugLog($"  - Vehicle Status: {vehicleStatus}");
                        DebugLog($"  - Driver Status: {driverStatus}");
                        DebugLog($"  - Protected Since: {protectedData.ProtectedSince:HH:mm:ss}");
                        DebugLog($"  - Force Count: {protectedData.ForceCount}");

                        if ((currentName != protectedData.ExpectedFullName ||
                             (_config.EnableGenderCorrection && currentGender != protectedData.ExpectedGender))
                            && protectedData.CurrentDriver?.Exists() == true)
                        {
                            DebugLog($"  *** MISMATCH DETECTED! ***");
                        }
                    }
                }

                DebugLog("=============================================");
            }
            catch (Exception ex)
            {
                DebugLog($"Error in DebugLogProtectedPlates: {ex.Message}");
            }
        }

        // Check for duplicate names and fix them
        private bool HandleDuplicateName(Ped ped, string firstName, string lastName, string originalPlate, string newPlate)
        {
            string nameKey = GetNameKey(firstName, lastName);

            lock (_usedNamesLock)
            {
                if (_usedNames.ContainsKey(nameKey))
                {
                    // Duplicate found!
                    Ped existingPed = _usedNames[nameKey];

                    if (existingPed.Exists() && existingPed != ped)
                    {
                        DebugLog($"DUPLICATE NAME DETECTED: {firstName} {lastName} already in use by another ped");

                        if (_config.AutoGenerateNamesForDuplicates)
                        {
                            // Generate a unique name
                            var uniqueName = GenerateUniqueName();
                            string newFirstName = uniqueName.Item1;
                            string newLastName = uniqueName.Item2;
                            string newGender = DetectGenderFromName(newFirstName);

                            DebugLog($"Assigning unique name: {newFirstName} {newLastName} (Gender: {newGender}) to ped (was {firstName} {lastName})");

                            // Queue the name force action
                            QueueNameForce(ped, newFirstName, newLastName, $"{newFirstName} {newLastName}",
                                newPlate, originalPlate, newGender);

                            _duplicateNamesFixed++;

                            // Add new name to used names
                            string newNameKey = GetNameKey(newFirstName, newLastName);
                            _usedNames[newNameKey] = ped;

                            return true;
                        }
                    }
                }
                else
                {
                    // No duplicate, add to used names
                    _usedNames[nameKey] = ped;
                }
            }

            return false;
        }

        // Get a unique key for a name
        private string GetNameKey(string firstName, string lastName)
        {
            return $"{firstName.ToUpper()}_{lastName.ToUpper()}";
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

        // Clean up dead protected plates
        private void CleanupProtectedPlates()
        {
            try
            {
                lock (_protectedPlatesLock)
                {
                    var deadPlates = _protectedPlates
                        .Where(kvp => kvp.Value.CurrentVehicle == null || !kvp.Value.CurrentVehicle.Exists())
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var plate in deadPlates)
                    {
                        _protectedPlates.Remove(plate);
                    }

                    if (deadPlates.Count > 0)
                    {
                        DebugLog($"Removed {deadPlates.Count} dead plates from protection list");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error in CleanupProtectedPlates: {ex.Message}");
            }
        }

        // Clean up dead peds from used names
        private void CleanupUsedNames()
        {
            try
            {
                lock (_usedNamesLock)
                {
                    var deadPeds = _usedNames.Where(kvp => !kvp.Value.Exists()).Select(kvp => kvp.Key).ToList();
                    foreach (var nameKey in deadPeds)
                    {
                        _usedNames.Remove(nameKey);
                    }
                    if (deadPeds.Count > 0)
                    {
                        DebugLog($"Removed {deadPeds.Count} dead peds from used names");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error in CleanupUsedNames: {ex.Message}");
            }
        }

        // Clean up vehicle tracking
        private void CleanupVehicleTracking()
        {
            try
            {
                lock (_vehicleLock)
                {
                    var deadVehicles = _vehicleCurrentDriver.Keys.Where(v => !v.Exists()).ToList();
                    foreach (var vehicle in deadVehicles)
                    {
                        _vehicleCurrentDriver.Remove(vehicle);
                        _vehicleCurrentPlate.Remove(vehicle);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error in CleanupVehicleTracking: {ex.Message}");
            }
        }

        // Check if model update is actually needed
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
                // Run normal vehicle scan
                if ((DateTime.Now - _lastScan).TotalMilliseconds > _config.ScanIntervalMs)
                {
                    ScanNearbyVehicles();
                    _lastScan = DateTime.Now;
                }

                // Process any pending name protection actions (on main thread)
                ProcessNameActions();

                // Check for name changes and driver changes every second
                CheckAndRestoreNames();
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
                int duplicatesFixed = 0;

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

                        // Get vehicle ID for tracking
                        int vehicleId = vehicle.GetHashCode();

                        // Check if this vehicle has already been processed permanently
                        string currentPlate = vehicle.LicensePlate;
                        string currentPlateClean = CleanPlate(currentPlate);

                        if (_processedVehicleIds.Contains(vehicleId))
                        {
                            DebugLog($"Vehicle {currentPlateClean} (ID:{vehicleId}) already permanently processed - skipping");
                            continue;
                        }

                        // Check if this plate has been permanently changed
                        if (_permanentlyChangedPlates.Contains(currentPlateClean))
                        {
                            DebugLog($"Plate {currentPlateClean} has already been permanently changed - skipping");
                            _processedVehicleIds.Add(vehicleId);
                            continue;
                        }

                        // Check if vehicle was processed too recently
                        if (_lastVehicleProcessTime.TryGetValue(vehicleId, out DateTime lastProcessed))
                        {
                            if ((DateTime.Now - lastProcessed).TotalSeconds < _config.VehicleProcessCooldownSeconds)
                            {
                                _vehicleCooldownSkips++;
                                DebugLog($"Skipping vehicle {currentPlateClean} - processed {DateTime.Now - lastProcessed:F1}s ago");
                                continue;
                            }
                        }

                        // Skip emergency vehicles if configured
                        if (_config.IgnoreEmergencyVehicles && IsEmergencyVehicle(vehicle))
                        {
                            DebugLog($"Skipping emergency vehicle: {vehicle.Model.Name}");
                            continue;
                        }

                        string currentModel = vehicle.Model.Name?.ToLower() ?? "";

                        // Get the driver
                        Ped driver = vehicle.Driver;
                        if (driver == null || !driver.Exists() || !driver.IsHuman)
                        {
                            DebugLog($"Vehicle {currentPlateClean} has no human driver");

                            // Still track the vehicle even without driver
                            lock (_vehicleLock)
                            {
                                _vehicleCurrentPlate[vehicle] = currentPlateClean;
                                _vehicleCurrentDriver[vehicle] = null;
                            }

                            _lastVehicleProcessTime[vehicleId] = DateTime.Now;
                            continue;
                        }

                        // Track vehicle and driver
                        lock (_vehicleLock)
                        {
                            _vehicleCurrentPlate[vehicle] = currentPlateClean;
                            _vehicleCurrentDriver[vehicle] = driver;
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

                        // Try to get person from cache first
                        var person = GetPersonFromCache(driverName, driver, vehicle);

                        if (person == null)
                        {
                            DebugLog($"No database entry found for '{driverName}'");

                            // Mark vehicle as processed even if no match
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

                                _lastVehicleProcessTime[vehicleId] = DateTime.Now;
                                continue;
                            }

                            // Get name parts
                            string[] nameParts = driverName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            string firstName = nameParts[0];
                            string lastName = nameParts.Length > 1 ? string.Join(" ", nameParts.Skip(1)) : "";

                            // Check for duplicate names before changing plate
                            bool duplicateHandled = HandleDuplicateName(driver, firstName, lastName, currentPlateClean, assignedPlateClean);

                            if (duplicateHandled)
                            {
                                duplicatesFixed++;
                                DebugLog($"Duplicate name handled for {driverName}");
                            }

                            // Log before plate change
                            DebugLog($"BEFORE PLATE CHANGE - Driver: {driverName}, Current Plate: {currentPlateClean}, Target Plate: {assignedPlateClean}");

                            // Change the IN-GAME plate
                            vehicle.LicensePlate = assignedPlate;
                            _processedCount++;
                            platesChanged++;

                            // Update vehicle tracking with new plate
                            lock (_vehicleLock)
                            {
                                _vehicleCurrentPlate[vehicle] = assignedPlateClean;
                            }

                            // Mark as permanently changed
                            _permanentlyChangedPlates.Add(assignedPlateClean);
                            _processedVehicleIds.Add(vehicleId);
                            _recentlyChangedPlates[assignedPlateClean] = DateTime.Now;

                            Log($"[VehicleAttach] PERMANENTLY changed plate for driver from '{currentPlate}' to '{assignedPlate}'");

                            // Get the correct name from the person object
                            string correctFirstName = "";
                            string correctLastName = "";

                            if (person.ContainsKey("first_name") && person["first_name"] != null)
                                correctFirstName = person["first_name"].ToString();
                            if (person.ContainsKey("last_name") && person["last_name"] != null)
                                correctLastName = person["last_name"].ToString();

                            if (!string.IsNullOrEmpty(correctFirstName) && !string.IsNullOrEmpty(correctLastName))
                            {
                                string correctFullName = $"{correctFirstName} {correctLastName}";
                                string correctGender = DetectGenderFromName(correctFirstName);

                                DebugLog($"Protecting plate {assignedPlateClean} for driver: {correctFullName} (Gender: {correctGender})");

                                // Add to protected plates
                                lock (_protectedPlatesLock)
                                {
                                    _protectedPlates[assignedPlateClean] = new ProtectedPlate
                                    {
                                        Plate = assignedPlateClean,
                                        ExpectedFirstName = correctFirstName,
                                        ExpectedLastName = correctLastName,
                                        ExpectedFullName = correctFullName,
                                        ExpectedGender = correctGender, // NEW: Store expected gender
                                        ProtectedSince = DateTime.Now,
                                        OriginalPlate = currentPlateClean,
                                        ForceCount = 0,
                                        LastForceTime = DateTime.Now,
                                        CurrentDriver = driver,
                                        CurrentVehicle = vehicle
                                    };
                                }

                                // Queue name force actions for current driver
                                for (int i = 0; i < _config.NameForceAttempts; i++)
                                {
                                    QueueNameForce(driver, correctFirstName, correctLastName,
                                        correctFullName, assignedPlateClean, currentPlateClean, correctGender);
                                }

                                // Queue a verification action
                                QueueNameVerification(driver, correctFirstName, correctLastName,
                                    correctFullName, assignedPlateClean, currentPlateClean, correctGender);

                                // Add to used names
                                string nameKey = GetNameKey(correctFirstName, correctLastName);
                                lock (_usedNamesLock)
                                {
                                    _usedNames[nameKey] = driver;
                                }
                            }

                            // Queue model update
                            if (QueueModelUpdate(assignedPlateClean, currentModel))
                                updatesQueued++;
                        }
                        else
                        {
                            DebugLog($"Plate already matches database: '{currentPlateClean}'");

                            // Check if this plate should be protected (if it's a permanently changed plate)
                            if (_permanentlyChangedPlates.Contains(currentPlateClean))
                            {
                                // Get the correct name from the person object
                                string correctFirstName = "";
                                string correctLastName = "";

                                if (person.ContainsKey("first_name") && person["first_name"] != null)
                                    correctFirstName = person["first_name"].ToString();
                                if (person.ContainsKey("last_name") && person["last_name"] != null)
                                    correctLastName = person["last_name"].ToString();

                                if (!string.IsNullOrEmpty(correctFirstName) && !string.IsNullOrEmpty(correctLastName))
                                {
                                    string correctFullName = $"{correctFirstName} {correctLastName}";
                                    string correctGender = DetectGenderFromName(correctFirstName);

                                    // Ensure plate is in protected list
                                    lock (_protectedPlatesLock)
                                    {
                                        if (!_protectedPlates.ContainsKey(currentPlateClean))
                                        {
                                            DebugLog($"Adding existing permanent plate {currentPlateClean} to protection list");
                                            _protectedPlates[currentPlateClean] = new ProtectedPlate
                                            {
                                                Plate = currentPlateClean,
                                                ExpectedFirstName = correctFirstName,
                                                ExpectedLastName = correctLastName,
                                                ExpectedFullName = correctFullName,
                                                ExpectedGender = correctGender,
                                                ProtectedSince = DateTime.Now,
                                                OriginalPlate = currentPlateClean,
                                                ForceCount = 0,
                                                LastForceTime = DateTime.Now,
                                                CurrentDriver = driver,
                                                CurrentVehicle = vehicle
                                            };
                                        }
                                        else
                                        {
                                            // Update driver info
                                            _protectedPlates[currentPlateClean].CurrentDriver = driver;
                                            _protectedPlates[currentPlateClean].CurrentVehicle = vehicle;
                                        }
                                    }
                                }
                            }

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

                            // Mark vehicle as processed
                            _lastVehicleProcessTime[vehicleId] = DateTime.Now;
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"Error processing vehicle: {ex.Message}");
                    }
                }

                DebugLog($"Scan complete: {platesChanged} plates permanently changed, {updatesQueued} updates queued, {duplicatesFixed} duplicates fixed out of {vehiclesScanned} scanned");
                DebugLog($"NAME STATS - ForceSuccess:{_nameForceSuccess} ForceFail:{_nameForceFailures} GenderCorrections:{_genderCorrections}");
                DebugLog($"CACHE STATS - Hits:{_cacheHits} Misses:{_cacheMisses} CooldownSkips:{_vehicleCooldownSkips}");
                DebugLog($"PLATE STATS - PermanentlyChanged:{_permanentlyChangedPlates.Count} ProcessedVehicles:{_processedVehicleIds.Count}");
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

        // Modified: Get person from cache or database with gender detection
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
                        DebugLog($"CACHE HIT: {driverName} (Gender: {cached.DetectedGender})");
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
            string detectedGender = DetectGenderFromName(firstName);

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
                    CachedAt = DateTime.Now,
                    DetectedGender = detectedGender // NEW: Store detected gender
                };
                DebugLog($"Cached {driverName} - Plate: {assignedPlate} (Gender: {detectedGender})");
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

        // Check for name changes and driver changes
        private void CheckAndRestoreNames()
        {
            try
            {
                if ((DateTime.Now - _lastNameProtectionCheck).TotalMilliseconds < _config.NameProtectionIntervalMs)
                    return;

                _lastNameProtectionCheck = DateTime.Now;

                // Check for driver changes
                CheckForDriverChanges();

                // Check protected plates
                CheckProtectedPlates();

                // Debug output
                DebugLogProtectedPlates();
            }
            catch (Exception ex)
            {
                DebugLog($"Error in CheckAndRestoreNames: {ex.Message}");
            }
        }

        // Generate a unique name
        private Tuple<string, string> GenerateUniqueName()
        {
            Random random = new Random();
            int maxAttempts = 100;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                string firstName = _availableFirstNames[random.Next(_availableFirstNames.Count)];
                string lastName = _availableLastNames[random.Next(_availableLastNames.Count)];
                string nameKey = GetNameKey(firstName, lastName);

                lock (_usedNamesLock)
                {
                    if (!_usedNames.ContainsKey(nameKey))
                    {
                        return new Tuple<string, string>(firstName, lastName);
                    }
                }
            }

            // Fallback: Use numbers
            return new Tuple<string, string>("Person", DateTime.Now.Ticks.ToString().Substring(8));
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
            _permanentlyChangedPlates.Clear();
            _processedVehicleIds.Clear();

            lock (_queueLock)
            {
                _pendingModelUpdates.Clear();
            }

            lock (_cacheLock)
            {
                _nameCache.Clear();
            }

            lock (_protectedPlatesLock)
            {
                _protectedPlates.Clear();
            }

            lock (_usedNamesLock)
            {
                _usedNames.Clear();
            }

            lock (_nameActionLock)
            {
                _pendingNameActions.Clear();
            }

            lock (_vehicleLock)
            {
                _vehicleCurrentDriver.Clear();
                _vehicleCurrentPlate.Clear();
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
            _permanentlyChangedPlates.Clear();
            _processedVehicleIds.Clear();

            lock (_cacheLock)
            {
                _nameCache.Clear();
            }

            lock (_protectedPlatesLock)
            {
                _protectedPlates.Clear();
            }

            lock (_usedNamesLock)
            {
                _usedNames.Clear();
            }

            lock (_nameActionLock)
            {
                _pendingNameActions.Clear();
            }

            lock (_vehicleLock)
            {
                _vehicleCurrentDriver.Clear();
                _vehicleCurrentPlate.Clear();
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
        public int GetNamesProtected() => _namesProtected;
        public int GetDuplicatesFixed() => _duplicateNamesFixed;
        public int GetPermanentlyChangedPlates() => _permanentlyChangedPlates.Count;
        public int GetNameForceSuccess() => _nameForceSuccess;
        public int GetNameForceFailures() => _nameForceFailures;
        public int GetProtectedPlatesCount()
        {
            lock (_protectedPlatesLock)
            {
                return _protectedPlates.Count;
            }
        }
        public int GetGenderCorrections() => _genderCorrections; // NEW

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

        public void SetAutoGenerateNames(bool enabled)
        {
            _config.AutoGenerateNamesForDuplicates = enabled;
            _config.Save(_configPath);
        }

        public void SetNameForceAttempts(int attempts)
        {
            _config.NameForceAttempts = attempts;
            _config.Save(_configPath);
        }

        // NEW: Gender correction settings
        public void SetEnableGenderCorrection(bool enabled)
        {
            _config.EnableGenderCorrection = enabled;
            _config.Save(_configPath);
        }

        public void SetChangePedModelToMatchGender(bool enabled)
        {
            _config.ChangePedModelToMatchGender = enabled;
            _config.Save(_configPath);
        }
    }
}