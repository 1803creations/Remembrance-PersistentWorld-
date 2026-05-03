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
        public int NameProtectionIntervalMs { get; set; } = 3000;
        public bool AutoGenerateNamesForDuplicates { get; set; } = true;
        public string DefaultFirstName { get; set; } = "John";
        public string DefaultLastName { get; set; } = "Doe";
        public int NameForceAttempts { get; set; } = 3;
        public bool EnableGenderCorrection { get; set; } = true;
        public bool ChangePedModelToMatchGender { get; set; } = true;
        public bool EnableFleetVehicles { get; set; } = true;
        public bool EnableIncarcerationReplacement { get; set; } = true;
        public bool UseDatabaseNamesForStolenVehicles { get; set; } = true;
        public int MaxStolenVehicleNameAttempts { get; set; } = 3;

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

    // XML Classes for Fleet Models
    [XmlRoot("FleetModels")]
    public class FleetModelCollection
    {
        [XmlElement("Model")]
        public List<FleetModelEntry> Models { get; set; } = new List<FleetModelEntry>();
    }

    public class FleetModelEntry
    {
        [XmlAttribute("model")]
        public string Model { get; set; }

        [XmlAttribute("company")]
        public string Company { get; set; }
    }

    // Class to track fleet vehicle assignments
    public class FleetVehicleAssignment
    {
        public string Plate { get; set; }
        public string Model { get; set; }
        public string Company { get; set; }
        public int VehicleId { get; set; }
        public DateTime AssignedAt { get; set; }
    }

    public class VehicleAttach
    {
        private DatabaseManager _database;
        private VehicleAttachConfig _config;
        private string _configPath;
        private string _databasePath;
        private DateTime _lastScan = DateTime.MinValue;
        private DateTime _lastNameProtectionCheck = DateTime.MinValue;
        private bool _isEnabled = true;
        private int _processedCount = 0;
        private int _modelUpdatesCompleted = 0;
        private int _namesProtected = 0;
        private int _duplicateNamesFixed = 0;
        private int _nameForceSuccess = 0;
        private int _nameForceFailures = 0;
        private int _genderCorrections = 0;
        private int _incarceratedReplaced = 0;
        private int _stolenVehiclesProcessed = 0;
        private int _fleetVehiclesProcessed = 0;

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

        // FLEET VEHICLE TRACKING
        private Dictionary<string, string> _fleetModelToCompany = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _fleetModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastFleetXmlLoad = DateTime.MinValue;
        private const int FLEET_XML_RELOAD_MINUTES = 5;
        private string _fleetXmlPath;

        // Track active fleet vehicle assignments
        private Dictionary<string, FleetVehicleAssignment> _activeFleetAssignments = new Dictionary<string, FleetVehicleAssignment>();
        private readonly object _fleetLock = new object();

        // Cache of available fleet plates per model
        private Dictionary<string, List<string>> _availableFleetPlates = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastFleetPlateLoad = DateTime.MinValue;
        private const int FLEET_PLATE_RELOAD_MINUTES = 10;

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
            "Ashley", "Jessica", "Amanda", "Brittany", "Courtney", "Heather", "Megan", "Nicole", "Rachel", "Danielle"
        };

        private List<string> _availableLastNames = new List<string>
        {
            "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez",
            "Wilson", "Anderson", "Taylor", "Thomas", "Moore", "Jackson", "Martin", "Lee", "Perez", "Thompson",
            "White", "Harris", "Sanchez", "Clark", "Ramirez", "Lewis", "Robinson", "Walker", "Young", "Allen",
            "King", "Wright", "Scott", "Torres", "Nguyen", "Hill", "Flores", "Green", "Adams", "Nelson",
            "Baker", "Hall", "Rivera", "Campbell", "Mitchell", "Carter", "Roberts", "Gomez", "Phillips", "Evans"
        };

        // Common male and female first names for gender detection
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

        // Common male and female ped models
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
            public string ExpectedGender { get; set; }
            public bool IsStolenVehicle { get; set; }  // New flag for stolen vehicles
        }

        // Cached person data
        private class CachedPerson
        {
            public Dictionary<string, object> Person { get; set; }
            public string AssignedPlate { get; set; }
            public string FullName { get; set; }
            public DateTime CachedAt { get; set; }
            public string DetectedGender { get; set; }
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
            public string TargetGender { get; set; }

            public enum ActionType
            {
                ForceName,
                VerifyName,
                CheckNewDriver
            }
        }

        public VehicleAttach(DatabaseManager database)
        {
            try
            {
                _database = database;

                // Store database path for creating fresh connections
                string gtaPath = AppDomain.CurrentDomain.BaseDirectory;
                _databasePath = Path.Combine(gtaPath, "Plugins", "LSPDFR", "PersistentWorld", "PersistentWorld.db");

                LoadConfiguration();

                // Set up fleet XML path
                _fleetXmlPath = Path.Combine(gtaPath, "Plugins", "LSPDFR", "PersistentWorld", "persistentworldfleet.xml");

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
                Game.LogTrivial("[VehicleAttach] Initialized");
                if (_config.EnableFleetVehicles)
                    Game.LogTrivial("[VehicleAttach] FLEET VEHICLE SYSTEM ENABLED");
                if (_config.EnableIncarcerationReplacement)
                    Game.LogTrivial("[VehicleAttach] INCARCERATION REPLACEMENT ENABLED");
                if (_config.UseDatabaseNamesForStolenVehicles)
                    Game.LogTrivial("[VehicleAttach] STOLEN VEHICLE DB NAME SYSTEM ENABLED");
                Game.LogTrivial("[VehicleAttach] ========================================");
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"[VehicleAttach] CRITICAL ERROR IN CONSTRUCTOR: {ex.Message}");
                Game.LogTrivial($"[VehicleAttach] Stack trace: {ex.StackTrace}");
            }
        }

        // ==================== SAFE CONVERSION METHODS ====================

        private int SafeConvertToInt(object value, int defaultValue = 0)
        {
            try
            {
                if (value == null || value == DBNull.Value)
                    return defaultValue;

                if (value is int)
                    return (int)value;

                if (value is long)
                    return (int)(long)value;

                if (value is short)
                    return (short)value;

                if (value is byte)
                    return (byte)value;

                if (value is string)
                {
                    if (int.TryParse((string)value, out int result))
                        return result;
                }

                return Convert.ToInt32(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        private long SafeConvertToLong(object value, long defaultValue = 0)
        {
            try
            {
                if (value == null || value == DBNull.Value)
                    return defaultValue;

                if (value is long)
                    return (long)value;

                if (value is int)
                    return (int)value;

                if (value is string)
                {
                    if (long.TryParse((string)value, out long result))
                        return result;
                }

                return Convert.ToInt64(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        private bool SafeConvertToBool(object value, bool defaultValue = false)
        {
            try
            {
                if (value == null || value == DBNull.Value)
                    return defaultValue;

                if (value is bool)
                    return (bool)value;

                if (value is int)
                    return (int)value == 1;

                if (value is long)
                    return (long)value == 1;

                if (value is short)
                    return (short)value == 1;

                if (value is byte)
                    return (byte)value == 1;

                if (value is string)
                {
                    string str = ((string)value).ToLower().Trim();
                    if (str == "true" || str == "yes" || str == "1" || str == "y")
                        return true;
                    if (str == "false" || str == "no" || str == "0" || str == "n")
                        return false;
                }

                return defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        private string SafeConvertToString(object value, string defaultValue = "")
        {
            try
            {
                if (value == null || value == DBNull.Value)
                    return defaultValue;

                return value.ToString();
            }
            catch
            {
                return defaultValue;
            }
        }

        private DateTime SafeConvertToDateTime(object value, DateTime defaultValue)
        {
            try
            {
                if (value == null || value == DBNull.Value)
                    return defaultValue;

                if (value is DateTime)
                    return (DateTime)value;

                if (value is string)
                {
                    if (DateTime.TryParse((string)value, out DateTime result))
                        return result;
                }

                return Convert.ToDateTime(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        private decimal SafeConvertToDecimal(object value, decimal defaultValue = 0)
        {
            try
            {
                if (value == null || value == DBNull.Value)
                    return defaultValue;

                if (value is decimal)
                    return (decimal)value;

                if (value is double)
                    return (decimal)(double)value;

                if (value is float)
                    return (decimal)(float)value;

                if (value is string)
                {
                    if (decimal.TryParse((string)value, out decimal result))
                        return result;
                }

                return Convert.ToDecimal(value);
            }
            catch
            {
                return defaultValue;
            }
        }

        // ==================== END SAFE CONVERSION METHODS ====================

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

        // Create a fresh database connection
        private SQLiteConnection CreateFreshConnection()
        {
            try
            {
                string connectionString = $"Data Source={_databasePath};Version=3;";
                var connection = new SQLiteConnection(connectionString);
                connection.Open();
                return connection;
            }
            catch (Exception ex)
            {
                DebugLog($"Error creating fresh connection: {ex.Message}");
                return null;
            }
        }

        // Check if a vehicle is stolen
        private bool IsVehicleStolen(Dictionary<string, object> vehicleData)
        {
            try
            {
                if (vehicleData == null) return false;

                if (vehicleData.ContainsKey("is_stolen") && vehicleData["is_stolen"] != null)
                {
                    return SafeConvertToBool(vehicleData["is_stolen"]);
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error checking stolen status: {ex.Message}");
            }

            return false;
        }

        // Check if a person is incarcerated
        private bool IsPersonIncarcerated(Dictionary<string, object> person)
        {
            try
            {
                if (person == null) return false;

                if (person.ContainsKey("is_incarcerated"))
                {
                    return SafeConvertToBool(person["is_incarcerated"]);
                }

                if (person.ContainsKey("incarcerated"))
                {
                    return SafeConvertToBool(person["incarcerated"]);
                }

                if (person.ContainsKey("incarcerated_days"))
                {
                    return SafeConvertToInt(person["incarcerated_days"]) > 0;
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error checking incarceration status: {ex.Message}");
            }

            return false;
        }

        // Get a random non-incarcerated person from database
        private Dictionary<string, object> GetRandomNonIncarceratedPersonFromDB()
        {
            try
            {
                using (var connection = CreateFreshConnection())
                {
                    if (connection == null)
                    {
                        DebugLog("Failed to create connection for random person query");
                        return null;
                    }

                    string query = @"
                        SELECT p.first_name, p.last_name, p.gender, p.model_name, p.is_incarcerated,
                               v.license_plate 
                        FROM persons p 
                        LEFT JOIN vehicles v ON p.id = v.owner_id 
                        WHERE p.is_incarcerated = 0 
                          AND p.first_name IS NOT NULL 
                          AND p.first_name != '' 
                          AND p.last_name IS NOT NULL 
                          AND p.last_name != ''
                        ORDER BY RANDOM() 
                        LIMIT 1";

                    using (var cmd = new SQLiteCommand(query, connection))
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            var person = new Dictionary<string, object>();
                            person["first_name"] = SafeConvertToString(reader["first_name"]);
                            person["last_name"] = SafeConvertToString(reader["last_name"]);
                            person["gender"] = SafeConvertToString(reader["gender"]);
                            person["model_name"] = SafeConvertToString(reader["model_name"]);
                            person["is_incarcerated"] = SafeConvertToBool(reader["is_incarcerated"]);

                            // Create a simple vehicle list for compatibility
                            var vehicles = new List<Dictionary<string, object>>();
                            var vehicle = new Dictionary<string, object>();
                            vehicle["license_plate"] = SafeConvertToString(reader["license_plate"]);
                            vehicles.Add(vehicle);
                            person["owned_vehicles"] = vehicles;

                            DebugLog($"Found random non-incarcerated person: {person["first_name"]} {person["last_name"]}");
                            return person;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error getting random person from DB: {ex.Message}");
            }

            return null;
        }

        // Get person from database excluding current person
        private Dictionary<string, object> GetRandomPersonExcludingCurrent(Dictionary<string, object> currentPerson)
        {
            try
            {
                string currentFirstName = SafeConvertToString(currentPerson.ContainsKey("first_name") ? currentPerson["first_name"] : null);
                string currentLastName = SafeConvertToString(currentPerson.ContainsKey("last_name") ? currentPerson["last_name"] : null);

                using (var connection = CreateFreshConnection())
                {
                    if (connection == null) return null;

                    string query = @"
                        SELECT first_name, last_name, gender, model_name, is_incarcerated
                        FROM persons 
                        WHERE NOT (first_name = @firstName AND last_name = @lastName)
                          AND first_name IS NOT NULL 
                          AND first_name != '' 
                          AND last_name IS NOT NULL 
                          AND last_name != ''
                          AND is_incarcerated = 0
                        ORDER BY RANDOM() 
                        LIMIT 1";

                    using (var cmd = new SQLiteCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@firstName", currentFirstName ?? "");
                        cmd.Parameters.AddWithValue("@lastName", currentLastName ?? "");

                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                var person = new Dictionary<string, object>();
                                person["first_name"] = SafeConvertToString(reader["first_name"]);
                                person["last_name"] = SafeConvertToString(reader["last_name"]);
                                person["gender"] = SafeConvertToString(reader["gender"]);
                                person["model_name"] = SafeConvertToString(reader["model_name"]);
                                person["is_incarcerated"] = SafeConvertToBool(reader["is_incarcerated"]);

                                DebugLog($"Found alternative person excluding {currentFirstName} {currentLastName}: {person["first_name"]} {person["last_name"]}");
                                return person;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error getting alternative person: {ex.Message}");
            }

            return null;
        }

        // Get all names from database
        private List<Tuple<string, string>> GetAllNamesFromDatabase()
        {
            var names = new List<Tuple<string, string>>();

            try
            {
                using (var connection = CreateFreshConnection())
                {
                    if (connection == null) return names;

                    string query = @"
                        SELECT DISTINCT first_name, last_name 
                        FROM persons 
                        WHERE first_name IS NOT NULL 
                          AND first_name != '' 
                          AND last_name IS NOT NULL 
                          AND last_name != ''
                        LIMIT 1000";

                    using (var cmd = new SQLiteCommand(query, connection))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string firstName = SafeConvertToString(reader["first_name"]);
                            string lastName = SafeConvertToString(reader["last_name"]);

                            if (!string.IsNullOrEmpty(firstName) && !string.IsNullOrEmpty(lastName))
                            {
                                names.Add(new Tuple<string, string>(firstName, lastName));
                            }
                        }
                    }
                }

                DebugLog($"Loaded {names.Count} unique names from database");
            }
            catch (Exception ex)
            {
                DebugLog($"Error loading names from database: {ex.Message}");
            }

            return names;
        }

        // Get a random non-incarcerated person of specific gender (fallback)
        private Dictionary<string, object> GetRandomNonIncarceratedPersonOfGender(string gender)
        {
            try
            {
                if (!_config.EnableIncarcerationReplacement)
                    return null;

                Random rand = new Random();

                for (int attempt = 0; attempt < 20; attempt++)
                {
                    string firstName = _availableFirstNames[rand.Next(_availableFirstNames.Count)];
                    string lastName = _availableLastNames[rand.Next(_availableLastNames.Count)];
                    string nameGender = DetectGenderFromName(firstName);

                    if (gender == "Unknown" || nameGender == gender)
                    {
                        DebugLog($"Creating new synthetic person: {firstName} {lastName}");

                        return new Dictionary<string, object>
                        {
                            ["first_name"] = firstName,
                            ["last_name"] = lastName,
                            ["gender"] = nameGender,
                            ["model_name"] = GetRandomPedModelForGender(nameGender),
                            ["license_status"] = "Valid",
                            ["owned_vehicles"] = new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                {
                                    ["license_plate"] = GenerateRandomSAPlate()
                                }
                            }
                        };
                    }
                }

                string fallbackFirstName = _availableFirstNames[rand.Next(_availableFirstNames.Count)];
                string fallbackLastName = _availableLastNames[rand.Next(_availableLastNames.Count)];
                string fallbackGender = DetectGenderFromName(fallbackFirstName);

                DebugLog($"Creating fallback synthetic person: {fallbackFirstName} {fallbackLastName}");

                return new Dictionary<string, object>
                {
                    ["first_name"] = fallbackFirstName,
                    ["last_name"] = fallbackLastName,
                    ["gender"] = fallbackGender,
                    ["model_name"] = GetRandomPedModelForGender(fallbackGender),
                    ["license_status"] = "Valid",
                    ["owned_vehicles"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["license_plate"] = GenerateRandomSAPlate()
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                DebugLog($"Error getting random non-incarcerated person: {ex.Message}");
                return null;
            }
        }

        // Load fleet XML
        private void LoadFleetXml()
        {
            try
            {
                if (!_config.EnableFleetVehicles) return;

                if ((DateTime.Now - _lastFleetXmlLoad).TotalMinutes < FLEET_XML_RELOAD_MINUTES)
                    return;

                if (!File.Exists(_fleetXmlPath))
                {
                    CreateDefaultFleetXml();
                    return;
                }

                var serializer = new XmlSerializer(typeof(FleetModelCollection));
                using (var reader = new StreamReader(_fleetXmlPath))
                {
                    var fleetData = (FleetModelCollection)serializer.Deserialize(reader);

                    lock (_fleetModelToCompany)
                    {
                        _fleetModelToCompany.Clear();
                        _fleetModels.Clear();

                        foreach (var entry in fleetData.Models)
                        {
                            if (!string.IsNullOrEmpty(entry.Model) && !string.IsNullOrEmpty(entry.Company))
                            {
                                string modelLower = entry.Model.ToLower();
                                _fleetModelToCompany[modelLower] = entry.Company;
                                _fleetModels.Add(modelLower);
                                DebugLog($"[Fleet] Loaded: {entry.Model} -> {entry.Company}");
                            }
                        }
                    }

                    _lastFleetXmlLoad = DateTime.Now;
                    Log($"[Fleet] Loaded {_fleetModelToCompany.Count} fleet model definitions");

                    LoadAvailableFleetPlates();
                }
            }
            catch (Exception ex)
            {
                Log($"[Fleet] Error loading fleet XML: {ex.Message}");
            }
        }

        // Create default fleet XML
        private void CreateDefaultFleetXml()
        {
            try
            {
                var defaultFleet = new FleetModelCollection
                {
                    Models = new List<FleetModelEntry>
                    {
                        new FleetModelEntry { Model = "transitadd1", Company = "First Energy" },
                        new FleetModelEntry { Model = "transitadd2", Company = "Spectrum Inc." },
                        new FleetModelEntry { Model = "transitadd3", Company = "Amazon Inc." },
                        new FleetModelEntry { Model = "transitadd4", Company = "Youtool Stores" },
                        new FleetModelEntry { Model = "transitadd5", Company = "Bugstars Co" },
                        new FleetModelEntry { Model = "muleadd1", Company = "CocoaCola Drinks" },
                        new FleetModelEntry { Model = "muleadd2", Company = "Pepsi Cola" },
                        new FleetModelEntry { Model = "muleadd3", Company = "Coors Brewing" }
                    }
                };

                var serializer = new XmlSerializer(typeof(FleetModelCollection));
                Directory.CreateDirectory(Path.GetDirectoryName(_fleetXmlPath));
                using (var writer = new StreamWriter(_fleetXmlPath))
                {
                    serializer.Serialize(writer, defaultFleet);
                }

                Log("[Fleet] Created default fleet XML");
            }
            catch (Exception ex)
            {
                Log($"[Fleet] Error creating default fleet XML: {ex.Message}");
            }
        }

        // Load all available fleet plates from database using fresh connection
        private void LoadAvailableFleetPlates()
        {
            try
            {
                if (!_config.EnableFleetVehicles) return;

                if ((DateTime.Now - _lastFleetPlateLoad).TotalMinutes < FLEET_PLATE_RELOAD_MINUTES)
                    return;

                lock (_fleetLock)
                {
                    _availableFleetPlates.Clear();

                    using (var connection = CreateFreshConnection())
                    {
                        if (connection == null)
                        {
                            DebugLog("[Fleet] Failed to create database connection");
                            return;
                        }

                        string query = @"
                            SELECT license_plate, vehicle_model 
                            FROM vehicles 
                            WHERE owner_type = 'company' 
                              AND is_active = 1
                            ORDER BY vehicle_model, license_plate";

                        using (var cmd = new SQLiteCommand(query, connection))
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string plate = SafeConvertToString(reader["license_plate"]);
                                string model = SafeConvertToString(reader["vehicle_model"]).ToLower();

                                if (!string.IsNullOrEmpty(plate) && !string.IsNullOrEmpty(model))
                                {
                                    if (!_availableFleetPlates.ContainsKey(model))
                                        _availableFleetPlates[model] = new List<string>();

                                    _availableFleetPlates[model].Add(plate);
                                }
                            }
                        }
                    }

                    _lastFleetPlateLoad = DateTime.Now;

                    foreach (var kvp in _availableFleetPlates)
                    {
                        DebugLog($"[Fleet] Model {kvp.Key} has {kvp.Value.Count} available plates");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[Fleet] Error loading fleet plates: {ex.Message}");
            }
        }

        // Get an unused fleet plate for a model
        private string GetUnusedFleetPlate(string model)
        {
            try
            {
                lock (_fleetLock)
                {
                    string modelLower = model.ToLower();

                    if (!_availableFleetPlates.ContainsKey(modelLower))
                    {
                        DebugLog($"[Fleet] No plates available for model: {model}");
                        return null;
                    }

                    var availablePlates = _availableFleetPlates[modelLower]
                        .Where(p => !_activeFleetAssignments.ContainsKey(p))
                        .ToList();

                    if (availablePlates.Count == 0)
                    {
                        DebugLog($"[Fleet] All {_availableFleetPlates[modelLower].Count} plates for {model} are currently in use");
                        return null;
                    }

                    Random random = new Random();
                    string selectedPlate = availablePlates[random.Next(availablePlates.Count)];

                    DebugLog($"[Fleet] Selected unused plate {selectedPlate} for model {model}");
                    return selectedPlate;
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[Fleet] Error getting unused plate: {ex.Message}");
                return null;
            }
        }

        // Clean up fleet assignments
        private void CleanupFleetAssignments()
        {
            try
            {
                lock (_fleetLock)
                {
                    var allVehicles = World.GetAllVehicles();
                    if (allVehicles == null) return;

                    var deadAssignments = _activeFleetAssignments
                        .Where(kvp => !allVehicles.Any(v => v != null && v.Exists() && CleanPlate(v.LicensePlate) == kvp.Key))
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var plate in deadAssignments)
                    {
                        _activeFleetAssignments.Remove(plate);
                        DebugLog($"[Fleet] Freed up plate {plate}");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error cleaning up fleet assignments: {ex.Message}");
            }
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
                        if (_config.EnableFleetVehicles)
                            CleanupFleetAssignments();

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
                            int fleetCount = 0;
                            lock (_fleetLock)
                            {
                                fleetCount = _activeFleetAssignments.Count;
                            }
                            DebugLog($"CACHE STATS - Size:{cacheSize} Hits:{_cacheHits} Misses:{_cacheMisses} HitRate:{(int)((float)_cacheHits / (_cacheHits + _cacheMisses + 1) * 100)}%");
                            DebugLog($"PROTECTION STATS - Protected Plates:{protectedCount} NamesProtected:{_namesProtected} ForceSuccess:{_nameForceSuccess} ForceFail:{_nameForceFailures} GenderCorrections:{_genderCorrections} IncarceratedReplaced:{_incarceratedReplaced} StolenVehicles:{_stolenVehiclesProcessed}");
                            DebugLog($"FLEET STATS - Active Fleet Vehicles:{fleetCount} Total Processed:{_fleetVehiclesProcessed}");
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
                        if (action?.TargetPed == null || !action.TargetPed.Exists()) continue;

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

        // Queue a name force action
        private void QueueNameForce(Ped ped, string firstName, string lastName, string fullName,
            string plate, string originalPlate, string targetGender)
        {
            if (ped == null) return;

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
            if (ped == null) return;

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
            if (ped == null) return;

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

        // Detect gender from first name
        private string DetectGenderFromName(string firstName)
        {
            if (string.IsNullOrEmpty(firstName))
                return "Unknown";

            string lowerFirstName = firstName.ToLower();

            if (_maleFirstNames.Contains(lowerFirstName))
                return "Male";

            if (_femaleFirstNames.Contains(lowerFirstName))
                return "Female";

            if (lowerFirstName.EndsWith("a") || lowerFirstName.EndsWith("e") ||
                lowerFirstName.EndsWith("y") || lowerFirstName == "ashley")
                return "Female";

            return "Unknown";
        }

        // Get random ped model for gender
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

            return gender == "Male" ? "a_m_m_beach_01" : "a_f_m_beach_01";
        }

        // Change ped model to match gender
        private bool ChangePedModel(Ped ped, string targetGender)
        {
            try
            {
                if (ped == null || !ped.Exists()) return false;

                if (!_config.ChangePedModelToMatchGender)
                {
                    DebugLog("Ped model changing is disabled in config");
                    return false;
                }

                string targetModelName = GetRandomPedModelForGender(targetGender);

                DebugLog($"Attempting to change ped model to {targetModelName} for gender {targetGender}");

                Model targetModel = new Model(targetModelName);
                if (!targetModel.IsValid)
                {
                    DebugLog($"Invalid model: {targetModelName}");
                    return false;
                }

                targetModel.LoadAndWait();
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

        // Force a ped's name
        private bool ForcePedNameInternal(Ped ped, string firstName, string lastName, string targetGender)
        {
            try
            {
                if (ped == null || !ped.Exists()) return false;

                DebugLog($"Attempting to force name for ped to: {firstName} {lastName} (Target Gender: {targetGender})");

                Persona currentPersona = Functions.GetPersonaForPed(ped);
                if (currentPersona == null) return false;

                if (_config.EnableGenderCorrection && targetGender != "Unknown")
                {
                    string currentGender = currentPersona.Gender.ToString();

                    if (currentGender != targetGender)
                    {
                        DebugLog($"GENDER MISMATCH DETECTED! Current: {currentGender}, Target: {targetGender}");

                        if (_config.ChangePedModelToMatchGender)
                        {
                            ChangePedModel(ped, targetGender);
                        }

                        Gender newGender = targetGender == "Male" ? Gender.Male : Gender.Female;
                        Persona newPersona = new Persona(firstName, lastName, newGender);
                        Functions.SetPersonaForPed(ped, newPersona);
                    }
                    else
                    {
                        Persona newPersona = new Persona(firstName, lastName, currentPersona.Gender);
                        Functions.SetPersonaForPed(ped, newPersona);
                    }
                }
                else
                {
                    Persona newPersona = new Persona(firstName, lastName, currentPersona.Gender);
                    Functions.SetPersonaForPed(ped, newPersona);
                }

                Persona afterPersona = Functions.GetPersonaForPed(ped);
                if (afterPersona != null)
                {
                    DebugLog($"Name after force: {afterPersona.FullName} (Gender: {afterPersona.Gender})");

                    if (afterPersona.FullName == $"{firstName} {lastName}")
                    {
                        DebugLog($"Name force SUCCESSFUL");
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

                        if (vehicle == null || !vehicle.Exists())
                        {
                            vehiclesToRemove.Add(vehicle);
                            continue;
                        }

                        Ped currentDriver = null;
                        try
                        {
                            currentDriver = vehicle.Driver;
                        }
                        catch
                        {
                            // Vehicle might be invalid
                            vehiclesToRemove.Add(vehicle);
                            continue;
                        }

                        // If driver changed
                        if (currentDriver != lastDriver)
                        {
                            string plate = CleanPlate(vehicle.LicensePlate);

                            _vehicleCurrentDriver[vehicle] = currentDriver;

                            lock (_protectedPlatesLock)
                            {
                                if (_protectedPlates.ContainsKey(plate))
                                {
                                    ProtectedPlate protectedData = _protectedPlates[plate];

                                    if (currentDriver != null && currentDriver.Exists())
                                    {
                                        DebugLog($"DRIVER CHANGE DETECTED for protected plate {plate}");
                                        protectedData.CurrentDriver = currentDriver;
                                        protectedData.CurrentVehicle = vehicle;

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

        // Check protected plates and ensure drivers have correct names and genders
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

                        if (protectedData.CurrentVehicle == null || !protectedData.CurrentVehicle.Exists())
                        {
                            Vehicle foundVehicle = null;
                            var allVehicles = World.GetAllVehicles();
                            if (allVehicles != null)
                            {
                                foreach (var vehicle in allVehicles)
                                {
                                    if (vehicle != null && vehicle.Exists() && CleanPlate(vehicle.LicensePlate) == plate)
                                    {
                                        foundVehicle = vehicle;
                                        break;
                                    }
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
                                platesToRemove.Add(plate);
                                DebugLog($"Removing protection for plate {plate} - vehicle no longer exists");
                                continue;
                            }
                        }

                        Ped currentDriver = null;
                        try
                        {
                            currentDriver = protectedData.CurrentVehicle.Driver;
                        }
                        catch
                        {
                            platesToRemove.Add(plate);
                            continue;
                        }

                        if (currentDriver != null && currentDriver.Exists())
                        {
                            if (currentDriver != protectedData.CurrentDriver)
                            {
                                protectedData.CurrentDriver = currentDriver;
                                DebugLog($"Driver changed for protected plate {plate} - new driver detected");
                            }

                            Persona persona = Functions.GetPersonaForPed(currentDriver);
                            if (persona != null)
                            {
                                string currentName = persona.FullName;
                                string expectedName = protectedData.ExpectedFullName;
                                string currentGender = persona.Gender.ToString();
                                string expectedGender = protectedData.ExpectedGender;

                                bool needsCorrection = false;

                                if (currentName != expectedName)
                                {
                                    DebugLog($"NAME MISMATCH for {(protectedData.IsStolenVehicle ? "STOLEN " : "")}vehicle {plate}!");
                                    DebugLog($"  Current: {currentName}, Expected: {expectedName}");
                                    needsCorrection = true;
                                }

                                if (_config.EnableGenderCorrection && expectedGender != "Unknown" && currentGender != expectedGender)
                                {
                                    DebugLog($"GENDER MISMATCH for {(protectedData.IsStolenVehicle ? "STOLEN " : "")}vehicle {plate}!");
                                    needsCorrection = true;
                                }

                                if (needsCorrection)
                                {
                                    DebugLog($"  - Forcing correction...");

                                    // Stolen vehicles get more forceful correction
                                    int forceAttempts = protectedData.IsStolenVehicle ?
                                        Math.Max(5, _config.NameForceAttempts) : _config.NameForceAttempts;

                                    for (int i = 0; i < forceAttempts; i++)
                                    {
                                        QueueNameForce(currentDriver,
                                            protectedData.ExpectedFirstName,
                                            protectedData.ExpectedLastName,
                                            expectedName, plate, protectedData.OriginalPlate,
                                            expectedGender);
                                    }

                                    if (protectedData.IsStolenVehicle)
                                    {
                                        DebugLog($"STOLEN VEHICLE CORRECTION: Forcing alternative name/gender for plate {plate}");
                                    }

                                    protectedData.ForceCount++;
                                    protectedData.LastForceTime = DateTime.Now;
                                    _namesProtected++;
                                }
                                else if (protectedData.IsStolenVehicle)
                                {
                                    DebugLog($"STOLEN VEHICLE {plate}: Name correct - alternative driver {currentName} is properly assigned");
                                }
                            }
                        }
                    }

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

        // Debug method to log all protected plates
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

                        DebugLog($"PLATE: {plate} {(protectedData.IsStolenVehicle ? "(STOLEN)" : "")}");
                        DebugLog($"  - Expected Name: {protectedData.ExpectedFullName}");
                        DebugLog($"  - Expected Gender: {protectedData.ExpectedGender}");
                        DebugLog($"  - Current Name: {currentName}");
                        DebugLog($"  - Current Gender: {currentGender}");
                        DebugLog($"  - Original Plate: {protectedData.OriginalPlate}");
                        DebugLog($"  - Vehicle Status: {vehicleStatus}");
                        DebugLog($"  - Driver Status: {driverStatus}");
                        DebugLog($"  - Protected Since: {protectedData.ProtectedSince:HH:mm:ss}");
                        DebugLog($"  - Force Count: {protectedData.ForceCount}");
                        DebugLog($"  - Is Stolen Vehicle: {protectedData.IsStolenVehicle}");
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
            if (ped == null) return false;

            string nameKey = GetNameKey(firstName, lastName);

            lock (_usedNamesLock)
            {
                if (_usedNames.ContainsKey(nameKey))
                {
                    Ped existingPed = _usedNames[nameKey];

                    if (existingPed != null && existingPed.Exists() && existingPed != ped)
                    {
                        DebugLog($"DUPLICATE NAME DETECTED: {firstName} {lastName} already in use by another ped");

                        if (_config.AutoGenerateNamesForDuplicates)
                        {
                            var uniqueName = GenerateUniqueName();
                            string newFirstName = uniqueName.Item1;
                            string newLastName = uniqueName.Item2;
                            string newGender = DetectGenderFromName(newFirstName);

                            DebugLog($"Assigning unique name: {newFirstName} {newLastName} (Gender: {newGender}) to ped");

                            QueueNameForce(ped, newFirstName, newLastName, $"{newFirstName} {newLastName}",
                                newPlate, originalPlate, newGender);

                            _duplicateNamesFixed++;

                            string newNameKey = GetNameKey(newFirstName, newLastName);
                            _usedNames[newNameKey] = ped;

                            return true;
                        }
                    }
                }
                else
                {
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
                    var deadPeds = _usedNames.Where(kvp => kvp.Value == null || !kvp.Value.Exists()).Select(kvp => kvp.Key).ToList();
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
                    var deadVehicles = _vehicleCurrentDriver.Keys.Where(v => v == null || !v.Exists()).ToList();
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

                // Use a fresh connection for the update
                using (var connection = CreateFreshConnection())
                {
                    if (connection == null)
                    {
                        DebugLog($"Failed to create connection for model update");
                        return false;
                    }

                    string updateQuery = @"
                        UPDATE vehicles 
                        SET vehicle_model = @model,
                            last_modified = CURRENT_TIMESTAMP
                        WHERE license_plate = @plate 
                          AND is_active = 1 
                          AND owner_type = 'person'";

                    using (var cmd = new SQLiteCommand(updateQuery, connection))
                    {
                        cmd.Parameters.AddWithValue("@plate", update.Plate);
                        cmd.Parameters.AddWithValue("@model", update.Model);
                        cmd.ExecuteNonQuery();
                    }
                }

                _modelUpdatesCompleted++;
                DebugLog($"SUCCESS: Updated model for {update.Plate} to {update.Model}");

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

                ProcessNameActions();
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
                // ===== CRITICAL SAFETY CHECKS =====
                if (Game.LocalPlayer == null)
                {
                    DebugLog("ScanNearbyVehicles: Local player is null - skipping");
                    return;
                }

                if (Game.LocalPlayer.Character == null)
                {
                    DebugLog("ScanNearbyVehicles: Player character is null - skipping");
                    return;
                }

                if (!Game.LocalPlayer.Character.Exists())
                {
                    DebugLog("ScanNearbyVehicles: Player character doesn't exist - skipping");
                    return;
                }

                Vector3 playerPos = Game.LocalPlayer.Character.Position;
                int vehiclesScanned = 0;
                int platesChanged = 0;
                int updatesQueued = 0;
                int duplicatesFixed = 0;
                int fleetPlatesAssigned = 0;

                DebugLog("========== STARTING VEHICLE SCAN ==========");
                DebugLog($"Player position: {playerPos}");

                if (_config.EnableFleetVehicles)
                {
                    try
                    {
                        LoadFleetXml();
                        LoadAvailableFleetPlates();
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"Error loading fleet data: {ex.Message}");
                    }
                }

                // Get all vehicles safely
                Vehicle[] allVehicles = null;
                try
                {
                    allVehicles = World.GetAllVehicles();
                }
                catch (Exception ex)
                {
                    DebugLog($"Error getting vehicles: {ex.Message}");
                    return;
                }

                if (allVehicles == null)
                {
                    DebugLog("World.GetAllVehicles() returned null");
                    return;
                }

                foreach (var vehicle in allVehicles)
                {
                    try
                    {
                        // ===== VEHICLE NULL CHECKS =====
                        if (vehicle == null)
                        {
                            DebugLog("Vehicle is null - skipping");
                            continue;
                        }

                        if (!vehicle.Exists())
                        {
                            DebugLog("Vehicle doesn't exist - skipping");
                            continue;
                        }

                        // Distance check
                        float distance = Vector3.Distance(vehicle.Position, playerPos);
                        if (distance > _config.ScanRadius) continue;

                        vehiclesScanned++;
                        _totalVehiclesScanned++;

                        if (vehiclesScanned > _config.MaxVehiclesPerScan)
                        {
                            DebugLog($"Reached max vehicles per scan ({_config.MaxVehiclesPerScan})");
                            break;
                        }

                        // ===== PLATE CHECKS =====
                        string currentPlate = null;
                        try
                        {
                            currentPlate = vehicle.LicensePlate;
                        }
                        catch
                        {
                            DebugLog("Could not get license plate - skipping");
                            continue;
                        }

                        if (string.IsNullOrEmpty(currentPlate))
                        {
                            DebugLog("Vehicle has no license plate - skipping");
                            continue;
                        }

                        string currentPlateClean = CleanPlate(currentPlate);

                        int vehicleId = 0;
                        try
                        {
                            vehicleId = vehicle.GetHashCode();
                        }
                        catch
                        {
                            DebugLog("Could not get vehicle hash code - using plate hash");
                            vehicleId = currentPlateClean.GetHashCode();
                        }

                        if (_processedVehicleIds.Contains(vehicleId))
                        {
                            DebugLog($"Vehicle {currentPlateClean} (ID:{vehicleId}) already permanently processed - skipping");
                            continue;
                        }

                        if (_permanentlyChangedPlates.Contains(currentPlateClean))
                        {
                            DebugLog($"Plate {currentPlateClean} has already been permanently changed - skipping");
                            _processedVehicleIds.Add(vehicleId);
                            continue;
                        }

                        if (_lastVehicleProcessTime.TryGetValue(vehicleId, out DateTime lastProcessed))
                        {
                            if ((DateTime.Now - lastProcessed).TotalSeconds < _config.VehicleProcessCooldownSeconds)
                            {
                                _vehicleCooldownSkips++;
                                DebugLog($"Skipping vehicle {currentPlateClean} - processed {DateTime.Now - lastProcessed:F1}s ago");
                                continue;
                            }
                        }

                        if (_config.IgnoreEmergencyVehicles && IsEmergencyVehicle(vehicle))
                        {
                            DebugLog($"Skipping emergency vehicle: {vehicle.Model.Name}");
                            continue;
                        }

                        string currentModel = null;
                        try
                        {
                            currentModel = vehicle.Model.Name?.ToLower() ?? "";
                        }
                        catch
                        {
                            DebugLog("Could not get vehicle model - skipping");
                            continue;
                        }

                        bool isFleetModel = _config.EnableFleetVehicles && _fleetModels.Contains(currentModel);

                        Ped driver = null;
                        try
                        {
                            driver = vehicle.Driver;
                        }
                        catch
                        {
                            DebugLog("Could not get vehicle driver - skipping");
                            continue;
                        }

                        lock (_vehicleLock)
                        {
                            _vehicleCurrentPlate[vehicle] = currentPlateClean;
                            _vehicleCurrentDriver[vehicle] = driver;
                        }

                        // ===== FLEET VEHICLE HANDLING =====
                        if (isFleetModel)
                        {
                            DebugLog($"FLEET VEHICLE DETECTED: Model={currentModel}, Current Plate={currentPlateClean}");

                            string fleetAssignedPlate = GetUnusedFleetPlate(currentModel);

                            if (!string.IsNullOrEmpty(fleetAssignedPlate))
                            {
                                string fleetAssignedPlateClean = CleanPlate(fleetAssignedPlate);

                                if (currentPlateClean != fleetAssignedPlateClean)
                                {
                                    DebugLog($"Assigning fleet plate {fleetAssignedPlateClean} to {currentModel} vehicle (was {currentPlateClean})");

                                    try
                                    {
                                        vehicle.LicensePlate = fleetAssignedPlate;
                                    }
                                    catch (Exception ex)
                                    {
                                        DebugLog($"Failed to set license plate: {ex.Message}");
                                        continue;
                                    }

                                    platesChanged++;
                                    fleetPlatesAssigned++;
                                    _fleetVehiclesProcessed++;

                                    lock (_vehicleLock)
                                    {
                                        _vehicleCurrentPlate[vehicle] = fleetAssignedPlateClean;
                                    }

                                    _permanentlyChangedPlates.Add(fleetAssignedPlateClean);
                                    _processedVehicleIds.Add(vehicleId);
                                    _recentlyChangedPlates[fleetAssignedPlateClean] = DateTime.Now;

                                    lock (_fleetLock)
                                    {
                                        string company = _fleetModelToCompany.ContainsKey(currentModel) ? _fleetModelToCompany[currentModel] : "Unknown";
                                        _activeFleetAssignments[fleetAssignedPlateClean] = new FleetVehicleAssignment
                                        {
                                            Plate = fleetAssignedPlateClean,
                                            Model = currentModel,
                                            Company = company,
                                            VehicleId = vehicleId,
                                            AssignedAt = DateTime.Now
                                        };
                                    }

                                    Log($"[Fleet] Assigned plate {fleetAssignedPlateClean} to {currentModel} ({_fleetModelToCompany[currentModel]})");
                                }
                                else
                                {
                                    DebugLog($"Fleet vehicle already has correct plate: {currentPlateClean}");

                                    lock (_fleetLock)
                                    {
                                        if (!_activeFleetAssignments.ContainsKey(currentPlateClean))
                                        {
                                            string company = _fleetModelToCompany.ContainsKey(currentModel) ? _fleetModelToCompany[currentModel] : "Unknown";
                                            _activeFleetAssignments[currentPlateClean] = new FleetVehicleAssignment
                                            {
                                                Plate = currentPlateClean,
                                                Model = currentModel,
                                                Company = company,
                                                VehicleId = vehicleId,
                                                AssignedAt = DateTime.Now
                                            };
                                        }
                                    }

                                    _permanentlyChangedPlates.Add(currentPlateClean);
                                    _processedVehicleIds.Add(vehicleId);
                                }

                                if (QueueModelUpdate(fleetAssignedPlateClean, currentModel))
                                    updatesQueued++;

                                _lastVehicleProcessTime[vehicleId] = DateTime.Now;
                                continue;
                            }
                            else
                            {
                                DebugLog($"[Fleet] No available plates for model {currentModel} - cannot assign");
                            }
                        }

                        // ===== PERSONAL VEHICLE HANDLING =====
                        if (driver == null || !driver.Exists() || !driver.IsHuman)
                        {
                            DebugLog($"Vehicle {currentPlateClean} has no human driver");
                            _lastVehicleProcessTime[vehicleId] = DateTime.Now;
                            continue;
                        }

                        string driverName = GetPedNameFromPersona(driver);

                        if (string.IsNullOrEmpty(driverName))
                        {
                            DebugLog($"Could not get name for driver of {currentPlateClean}");
                            _lastVehicleProcessTime[vehicleId] = DateTime.Now;
                            continue;
                        }

                        DebugLog($"Driver found: '{driverName}' driving {vehicle.Model.Name} with plate '{currentPlate}'");
                        _totalNamesFound++;

                        var person = GetPersonFromCache(driverName, driver, vehicle);

                        if (person == null)
                        {
                            DebugLog($"No database entry found for '{driverName}'");
                            _lastVehicleProcessTime[vehicleId] = DateTime.Now;
                            continue;
                        }

                        _totalDatabaseMatches++;

                        string assignedPlate = GetAssignedPlateForPerson(person);
                        if (string.IsNullOrEmpty(assignedPlate))
                        {
                            DebugLog($"No assigned plate found for {driverName}");
                            _lastVehicleProcessTime[vehicleId] = DateTime.Now;
                            continue;
                        }

                        string assignedPlateClean = CleanPlate(assignedPlate);
                        DebugLog($"Assigned plate from database: '{assignedPlate}' (clean: '{assignedPlateClean}')");

                        // Check if vehicle is stolen first
                        bool isStolen = false;
                        string originalPlate = currentPlateClean;
                        var vehicleData = _database.GetVehicleByPlate(originalPlate);
                        if (vehicleData != null)
                        {
                            isStolen = IsVehicleStolen(vehicleData);
                        }

                        // ===== STOLEN VEHICLE HANDLING =====
                        if (isStolen)
                        {
                            DebugLog($"STOLEN VEHICLE DETECTED: {originalPlate} - finding alternative driver from database");

                            string stolenDriverFirstName = "";
                            string stolenDriverLastName = "";
                            string stolenDriverFullName = "";
                            string stolenDriverGender = "Unknown";

                            // Try to get a different person from the database (exclude the owner)
                            var alternativePerson = GetRandomPersonExcludingCurrent(person);

                            if (alternativePerson != null)
                            {
                                stolenDriverFirstName = SafeConvertToString(alternativePerson.ContainsKey("first_name") ? alternativePerson["first_name"] : null);
                                stolenDriverLastName = SafeConvertToString(alternativePerson.ContainsKey("last_name") ? alternativePerson["last_name"] : null);
                                stolenDriverFullName = $"{stolenDriverFirstName} {stolenDriverLastName}";
                                stolenDriverGender = DetectGenderFromName(stolenDriverFirstName);

                                DebugLog($"STOLEN VEHICLE: Assigned alternative DB driver {stolenDriverFullName} to stolen vehicle {originalPlate}");
                            }
                            else
                            {
                                // Fallback: use names from database if available
                                var namesFromDB = GetAllNamesFromDatabase();
                                if (namesFromDB.Count > 0)
                                {
                                    Random rand = new Random();
                                    var randomName = namesFromDB[rand.Next(namesFromDB.Count)];
                                    stolenDriverFirstName = randomName.Item1;
                                    stolenDriverLastName = randomName.Item2;
                                    stolenDriverFullName = $"{stolenDriverFirstName} {stolenDriverLastName}";
                                    stolenDriverGender = DetectGenderFromName(stolenDriverFirstName);
                                    DebugLog($"STOLEN VEHICLE: Using random DB name {stolenDriverFullName} for stolen vehicle {originalPlate}");
                                }
                                else
                                {
                                    // Ultimate fallback
                                    stolenDriverFirstName = _config.DefaultFirstName;
                                    stolenDriverLastName = _config.DefaultLastName;
                                    stolenDriverFullName = $"{stolenDriverFirstName} {stolenDriverLastName}";
                                    stolenDriverGender = DetectGenderFromName(stolenDriverFirstName);
                                    DebugLog($"STOLEN VEHICLE: Using default name {stolenDriverFullName} for stolen vehicle {originalPlate}");
                                }
                            }

                            // Force the stolen vehicle driver to have the alternative database name
                            for (int i = 0; i < Math.Max(5, _config.NameForceAttempts); i++)
                            {
                                QueueNameForce(driver, stolenDriverFirstName, stolenDriverLastName,
                                    stolenDriverFullName, originalPlate, originalPlate, stolenDriverGender);
                            }

                            // Add to protected plates with the ALTERNATIVE database name
                            lock (_protectedPlatesLock)
                            {
                                _protectedPlates[originalPlate] = new ProtectedPlate
                                {
                                    Plate = originalPlate,
                                    ExpectedFirstName = stolenDriverFirstName,    // Alternative DB name
                                    ExpectedLastName = stolenDriverLastName,       // Alternative DB name
                                    ExpectedFullName = stolenDriverFullName,      // Alternative DB name
                                    ExpectedGender = stolenDriverGender,
                                    ProtectedSince = DateTime.Now,
                                    OriginalPlate = originalPlate,
                                    ForceCount = 0,
                                    LastForceTime = DateTime.Now,
                                    CurrentDriver = driver,
                                    CurrentVehicle = vehicle,
                                    IsStolenVehicle = true
                                };
                            }

                            // Set up frequent verification for stolen vehicles (with alternative name)
                            QueueNameVerification(driver, stolenDriverFirstName, stolenDriverLastName,
                                stolenDriverFullName, originalPlate, originalPlate, stolenDriverGender);

                            // Use the alternative name for duplicate tracking
                            string stolenNameKey = GetNameKey(stolenDriverFirstName, stolenDriverLastName);
                            lock (_usedNamesLock)
                            {
                                _usedNames[stolenNameKey] = driver;
                            }

                            _stolenVehiclesProcessed++;
                            _lastVehicleProcessTime[vehicleId] = DateTime.Now;

                            // Mark as permanently processed to avoid re-scanning
                            _permanentlyChangedPlates.Add(originalPlate);
                            _processedVehicleIds.Add(vehicleId);
                            continue;
                        }

                        // ===== NORMAL VEHICLE HANDLING =====
                        if (currentPlateClean != assignedPlateClean)
                        {
                            DebugLog($"Plate mismatch: Current='{currentPlateClean}', Database='{assignedPlateClean}'");

                            if (_recentlyChangedPlates.ContainsKey(assignedPlateClean))
                            {
                                TimeSpan cooldownRemaining = DateTime.Now - _recentlyChangedPlates[assignedPlateClean];
                                DebugLog($"Plate {assignedPlateClean} is in cooldown ({cooldownRemaining.TotalSeconds:F1}s ago)");
                                _lastVehicleProcessTime[vehicleId] = DateTime.Now;
                                continue;
                            }

                            string[] nameParts = driverName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            string firstName = nameParts.Length > 0 ? nameParts[0] : "";
                            string lastName = nameParts.Length > 1 ? string.Join(" ", nameParts.Skip(1)) : "";

                            bool duplicateHandled = HandleDuplicateName(driver, firstName, lastName, currentPlateClean, assignedPlateClean);

                            if (duplicateHandled)
                            {
                                duplicatesFixed++;
                                DebugLog($"Duplicate name handled for {driverName}");
                            }

                            DebugLog($"BEFORE PLATE CHANGE - Driver: {driverName}, Current Plate: {currentPlateClean}, Target Plate: {assignedPlateClean}");

                            try
                            {
                                vehicle.LicensePlate = assignedPlate;
                            }
                            catch (Exception ex)
                            {
                                DebugLog($"Failed to set license plate: {ex.Message}");
                                continue;
                            }

                            _processedCount++;
                            platesChanged++;

                            lock (_vehicleLock)
                            {
                                _vehicleCurrentPlate[vehicle] = assignedPlateClean;
                            }

                            _permanentlyChangedPlates.Add(assignedPlateClean);
                            _processedVehicleIds.Add(vehicleId);
                            _recentlyChangedPlates[assignedPlateClean] = DateTime.Now;

                            Log($"[VehicleAttach] PERMANENTLY changed plate for driver from '{currentPlate}' to '{assignedPlate}'");

                            string correctFirstName = SafeConvertToString(person.ContainsKey("first_name") ? person["first_name"] : null);
                            string correctLastName = SafeConvertToString(person.ContainsKey("last_name") ? person["last_name"] : null);

                            if (!string.IsNullOrEmpty(correctFirstName) && !string.IsNullOrEmpty(correctLastName))
                            {
                                string correctFullName = $"{correctFirstName} {correctLastName}";
                                string correctGender = DetectGenderFromName(correctFirstName);

                                DebugLog($"Protecting plate {assignedPlateClean} for driver: {correctFullName} (Gender: {correctGender})");

                                lock (_protectedPlatesLock)
                                {
                                    _protectedPlates[assignedPlateClean] = new ProtectedPlate
                                    {
                                        Plate = assignedPlateClean,
                                        ExpectedFirstName = correctFirstName,
                                        ExpectedLastName = correctLastName,
                                        ExpectedFullName = correctFullName,
                                        ExpectedGender = correctGender,
                                        ProtectedSince = DateTime.Now,
                                        OriginalPlate = currentPlateClean,
                                        ForceCount = 0,
                                        LastForceTime = DateTime.Now,
                                        CurrentDriver = driver,
                                        CurrentVehicle = vehicle,
                                        IsStolenVehicle = false
                                    };
                                }

                                for (int i = 0; i < _config.NameForceAttempts; i++)
                                {
                                    QueueNameForce(driver, correctFirstName, correctLastName,
                                        correctFullName, assignedPlateClean, currentPlateClean, correctGender);
                                }

                                QueueNameVerification(driver, correctFirstName, correctLastName,
                                    correctFullName, assignedPlateClean, currentPlateClean, correctGender);

                                string nameKey = GetNameKey(correctFirstName, correctLastName);
                                lock (_usedNamesLock)
                                {
                                    _usedNames[nameKey] = driver;
                                }
                            }

                            if (QueueModelUpdate(assignedPlateClean, currentModel))
                                updatesQueued++;
                        }
                        else
                        {
                            DebugLog($"Plate already matches database: '{currentPlateClean}'");

                            if (_permanentlyChangedPlates.Contains(currentPlateClean))
                            {
                                string correctFirstName = SafeConvertToString(person.ContainsKey("first_name") ? person["first_name"] : null);
                                string correctLastName = SafeConvertToString(person.ContainsKey("last_name") ? person["last_name"] : null);

                                if (!string.IsNullOrEmpty(correctFirstName) && !string.IsNullOrEmpty(correctLastName))
                                {
                                    string correctFullName = $"{correctFirstName} {correctLastName}";
                                    string correctGender = DetectGenderFromName(correctFirstName);

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
                                                CurrentVehicle = vehicle,
                                                IsStolenVehicle = false
                                            };
                                        }
                                        else
                                        {
                                            _protectedPlates[currentPlateClean].CurrentDriver = driver;
                                            _protectedPlates[currentPlateClean].CurrentVehicle = vehicle;
                                        }
                                    }
                                }
                            }

                            if (DoesModelNeedUpdate(assignedPlateClean, currentModel))
                            {
                                if (QueueModelUpdate(assignedPlateClean, currentModel))
                                    updatesQueued++;
                            }
                            else
                            {
                                DebugLog($"Model already up to date for {assignedPlateClean}");
                            }

                            _lastVehicleProcessTime[vehicleId] = DateTime.Now;
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"Error processing vehicle: {ex.Message}");
                    }
                }

                DebugLog($"Scan complete: {platesChanged} plates permanently changed ({fleetPlatesAssigned} fleet), {updatesQueued} updates queued, {duplicatesFixed} duplicates fixed out of {vehiclesScanned} scanned");
                DebugLog($"FLEET STATS - Active Assignments:{_activeFleetAssignments.Count} Total Processed:{_fleetVehiclesProcessed}");
                DebugLog($"STOLEN VEHICLES - Processed:{_stolenVehiclesProcessed}");
                DebugLog($"NAME STATS - ForceSuccess:{_nameForceSuccess} ForceFail:{_nameForceFailures} GenderCorrections:{_genderCorrections} IncarceratedReplaced:{_incarceratedReplaced}");
                DebugLog($"CACHE STATS - Hits:{_cacheHits} Misses:{_cacheMisses} CooldownSkips:{_vehicleCooldownSkips}");
                DebugLog($"PLATE STATS - PermanentlyChanged:{_permanentlyChangedPlates.Count} ProcessedVehicles:{_processedVehicleIds.Count}");
                DebugLog("========== SCAN END ==========");
            }
            catch (Exception ex)
            {
                Log($"[VehicleAttach] CRITICAL ERROR in scan: {ex.Message}");
                DebugLog($"Stack trace: {ex.StackTrace}");
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

                    var existingUpdate = _pendingModelUpdates.FirstOrDefault(u => u.Plate == plate);

                    if (existingUpdate != null)
                    {
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

                    _pendingModelUpdates.Enqueue(new ModelUpdate
                    {
                        Plate = plate,
                        Model = model,
                        RetryCount = 0,
                        FirstAttempt = DateTime.Now,
                        LastAttempt = DateTime.Now
                    });

                    lock (_lastKnownModel)
                    {
                        _lastKnownModel[plate] = model;
                    }

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

        // Get person from cache or database with safe conversions, incarceration check, and stolen handling
        private Dictionary<string, object> GetPersonFromCache(string driverName, Ped driver, Vehicle vehicle)
        {
            if (string.IsNullOrEmpty(driverName)) return null;

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

            if (_config.EnableIncarcerationReplacement && IsPersonIncarcerated(person))
            {
                DebugLog($"INCARCERATED PERSON DETECTED: {firstName} {lastName} - finding replacement");

                var replacement = GetRandomNonIncarceratedPersonOfGender(detectedGender);

                if (replacement != null)
                {
                    DebugLog($"Replacing incarcerated person with: {SafeConvertToString(replacement.ContainsKey("first_name") ? replacement["first_name"] : null)} {SafeConvertToString(replacement.ContainsKey("last_name") ? replacement["last_name"] : null)}");
                    person = replacement;
                    _incarceratedReplaced++;
                }
            }

            lock (_cacheLock)
            {
                _nameCache[cacheKey] = new CachedPerson
                {
                    Person = person,
                    AssignedPlate = GetAssignedPlateForPerson(person),
                    FullName = driverName,
                    CachedAt = DateTime.Now,
                    DetectedGender = detectedGender
                };
            }

            return person;
        }

        private Dictionary<string, object> FindBestMatchingPerson(List<Dictionary<string, object>> persons, Ped driver, Vehicle vehicle)
        {
            try
            {
                if (persons == null || persons.Count == 0)
                    return null;

                if (persons.Count == 1)
                {
                    DebugLog("Only one match found, using it");
                    return persons[0];
                }

                DebugLog($"Found {persons.Count} matches for driver, attempting to find best match");

                string driverModel = "";
                if (driver != null)
                {
                    try
                    {
                        driverModel = driver.Model.Name.ToLower();
                    }
                    catch
                    {
                        driverModel = "";
                    }
                }

                if (!string.IsNullOrEmpty(driverModel))
                {
                    foreach (var person in persons)
                    {
                        try
                        {
                            if (person == null) continue;

                            if (person.ContainsKey("model_name") && person["model_name"] != null)
                            {
                                string dbModel = SafeConvertToString(person["model_name"]).ToLower();
                                if (!string.IsNullOrEmpty(dbModel) && dbModel == driverModel)
                                {
                                    DebugLog($"Matched by ped model: driver={driverModel}, db={dbModel}");
                                    return person;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            DebugLog($"Error checking person model: {ex.Message}");
                            continue;
                        }
                    }
                }

                DebugLog($"Could not determine best match, using first");
                return persons[0];
            }
            catch (Exception ex)
            {
                DebugLog($"Error in FindBestMatchingPerson: {ex.Message}");
                return persons?.FirstOrDefault();
            }
        }

        private string GetAssignedPlateForPerson(Dictionary<string, object> person)
        {
            try
            {
                if (person == null)
                {
                    DebugLog("Person is null");
                    return null;
                }

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
                if (firstVehicle == null) return null;

                if (firstVehicle.ContainsKey("license_plate"))
                {
                    string plate = SafeConvertToString(firstVehicle["license_plate"]);
                    if (!string.IsNullOrEmpty(plate))
                    {
                        DebugLog($"First vehicle plate: '{plate}'");
                        return plate;
                    }
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
                if (vehicle == null) return false;

                if (vehicle.Class == VehicleClass.Emergency) return true;

                string modelName = vehicle.Model.Name?.ToLower() ?? "";

                if (modelName.Contains("police") ||
                    modelName.Contains("sheriff") ||
                    modelName.Contains("fbi") ||
                    modelName.Contains("fire") ||
                    modelName.Contains("ambulance") ||
                    modelName.Contains("riot") ||
                    modelName.Contains("pranger") ||
                    modelName.Contains("bcsd") ||
                    modelName.Contains("ohsp") ||
                    modelName.Contains("osp") ||
                    modelName.Contains("ranger"))
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
                if (ped == null) return "";

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

        private void CheckAndRestoreNames()
        {
            try
            {
                if ((DateTime.Now - _lastNameProtectionCheck).TotalMilliseconds < _config.NameProtectionIntervalMs)
                    return;

                _lastNameProtectionCheck = DateTime.Now;

                CheckForDriverChanges();
                CheckProtectedPlates();
                DebugLogProtectedPlates();
            }
            catch (Exception ex)
            {
                DebugLog($"Error in CheckAndRestoreNames: {ex.Message}");
            }
        }

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

            lock (_fleetLock)
            {
                _activeFleetAssignments.Clear();
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

            lock (_fleetLock)
            {
                _activeFleetAssignments.Clear();
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
        public int GetFleetVehiclesCount()
        {
            lock (_fleetLock)
            {
                return _activeFleetAssignments.Count;
            }
        }
        public int GetFleetVehiclesProcessed() => _fleetVehiclesProcessed;
        public int GetGenderCorrections() => _genderCorrections;
        public int GetIncarceratedReplaced() => _incarceratedReplaced;
        public int GetStolenVehiclesProcessed() => _stolenVehiclesProcessed;

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

        public void SetEnableFleetVehicles(bool enabled)
        {
            _config.EnableFleetVehicles = enabled;
            _config.Save(_configPath);
        }

        public void SetEnableIncarcerationReplacement(bool enabled)
        {
            _config.EnableIncarcerationReplacement = enabled;
            _config.Save(_configPath);
        }

        public void SetUseDatabaseNamesForStolenVehicles(bool enabled)
        {
            _config.UseDatabaseNamesForStolenVehicles = enabled;
            _config.Save(_configPath);
        }
    }
}
