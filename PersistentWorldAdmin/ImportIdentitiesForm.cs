using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PersistentWorldAdmin
{
    public partial class ImportIdentitiesForm : Form
    {
        private SQLiteConnection _connection;
        private List<IdentityJson> _identities;
        private int _currentIndex = 0;
        private int _imported = 0;
        private int _duplicates = 0;
        private int _skipped = 0;
        private int _errors = 0;
        private Random _random = new Random();

        // Controls
        private TextBox txtFilePath;
        private Button btnBrowse;
        private Button btnLoad;
        private Button btnStart;
        private Button btnCancel;
        private ProgressBar progressBar;
        private Label lblStatus;
        private Label lblStats;
        private DataGridView gridPreview;
        private CheckBox chkSkipDuplicates;
        private CheckBox chkPreviewOnly;
        private ComboBox cmbDefaultModel;
        private NumericUpDown numDefaultHomePercent;
        private NumericUpDown numDefaultDrivingPercent;
        private NumericUpDown numDefaultWorldPercent;

        public ImportIdentitiesForm(SQLiteConnection connection)
        {
            _connection = connection;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Import Identities from JSON";
            this.Size = new Size(900, 700);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            // File selection panel
            var filePanel = new Panel { Dock = DockStyle.Top, Height = 80, Padding = new Padding(10) };

            var lblFile = new Label { Text = "JSON File:", Location = new Point(10, 15), Size = new Size(70, 25) };
            txtFilePath = new TextBox { Location = new Point(90, 15), Size = new Size(500, 25) };
            btnBrowse = new Button { Text = "Browse...", Location = new Point(600, 15), Size = new Size(100, 25) };
            btnBrowse.Click += BtnBrowse_Click;

            btnLoad = new Button { Text = "Load File", Location = new Point(710, 15), Size = new Size(100, 25), Enabled = false };
            btnLoad.Click += BtnLoad_Click;

            filePanel.Controls.AddRange(new Control[] { lblFile, txtFilePath, btnBrowse, btnLoad });

            // Options panel
            var optionsPanel = new Panel { Dock = DockStyle.Top, Height = 120, Padding = new Padding(10) };

            var lblOptions = new Label { Text = "Import Options:", Location = new Point(10, 5), Size = new Size(200, 20), Font = new Font("Arial", 10, FontStyle.Bold) };

            chkSkipDuplicates = new CheckBox { Text = "Skip duplicate names", Location = new Point(20, 30), Size = new Size(200, 25), Checked = true };
            chkPreviewOnly = new CheckBox { Text = "Preview only (don't import)", Location = new Point(20, 55), Size = new Size(200, 25) };

            var lblModel = new Label { Text = "Default model:", Location = new Point(250, 30), Size = new Size(100, 25) };
            cmbDefaultModel = new ComboBox { Location = new Point(360, 30), Size = new Size(200, 25), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbDefaultModel.Items.AddRange(new[] {
                "Use JSON model",
                "Random male",
                "Random female",
                "a_m_y_skater_01",
                "a_m_y_vinewood_01",
                "a_f_y_skater_01",
                "s_m_y_cop_01"
            });
            cmbDefaultModel.SelectedIndex = 0;

            var lblHomePct = new Label { Text = "Default home %:", Location = new Point(250, 60), Size = new Size(100, 25) };
            numDefaultHomePercent = new NumericUpDown { Location = new Point(360, 60), Size = new Size(60, 25), Minimum = 0, Maximum = 100, Value = 30 };

            var lblDrivePct = new Label { Text = "Default driving %:", Location = new Point(430, 60), Size = new Size(100, 25) };
            numDefaultDrivingPercent = new NumericUpDown { Location = new Point(540, 60), Size = new Size(60, 25), Minimum = 0, Maximum = 100, Value = 30 };

            var lblWorldPct = new Label { Text = "Default world %:", Location = new Point(610, 60), Size = new Size(100, 25) };
            numDefaultWorldPercent = new NumericUpDown { Location = new Point(720, 60), Size = new Size(60, 25), Minimum = 0, Maximum = 100, Value = 40 };

            optionsPanel.Controls.AddRange(new Control[] {
                lblOptions, chkSkipDuplicates, chkPreviewOnly,
                lblModel, cmbDefaultModel,
                lblHomePct, numDefaultHomePercent,
                lblDrivePct, numDefaultDrivingPercent,
                lblWorldPct, numDefaultWorldPercent
            });

            // Preview grid
            gridPreview = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells
            };

            // Status panel
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 100, Padding = new Padding(10) };

            progressBar = new ProgressBar { Location = new Point(10, 10), Size = new Size(860, 25), Minimum = 0, Maximum = 100 };

            lblStats = new Label
            {
                Location = new Point(10, 40),
                Size = new Size(600, 20),
                Text = "Ready to load file..."
            };

            lblStatus = new Label
            {
                Location = new Point(10, 65),
                Size = new Size(600, 20),
                Text = ""
            };

            btnStart = new Button { Text = "Start Import", Location = new Point(670, 40), Size = new Size(100, 30), Enabled = false };
            btnStart.Click += BtnStart_Click;

            btnCancel = new Button { Text = "Close", Location = new Point(780, 40), Size = new Size(100, 30) };
            btnCancel.Click += (s, e) => this.Close();

            statusPanel.Controls.AddRange(new Control[] { progressBar, lblStats, lblStatus, btnStart, btnCancel });

            // Add all panels to form
            this.Controls.Add(gridPreview);
            this.Controls.Add(statusPanel);
            this.Controls.Add(optionsPanel);
            this.Controls.Add(filePanel);
        }

        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*";
                dialog.Title = "Select Identities JSON File";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    txtFilePath.Text = dialog.FileName;
                    btnLoad.Enabled = true;
                }
            }
        }

        private void BtnLoad_Click(object sender, EventArgs e)
        {
            try
            {
                string jsonContent = File.ReadAllText(txtFilePath.Text);

                // Parse the JSON
                var jsonObject = JObject.Parse(jsonContent);

                // Find the array (it's under a weird key name)
                JArray identitiesArray = null;
                foreach (var property in jsonObject.Properties())
                {
                    if (property.Value.Type == JTokenType.Array)
                    {
                        identitiesArray = (JArray)property.Value;
                        break;
                    }
                }

                if (identitiesArray == null)
                {
                    MessageBox.Show("Could not find identities array in JSON", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                _identities = identitiesArray.ToObject<List<IdentityJson>>();

                lblStats.Text = $"Loaded {_identities.Count} identities from file";
                btnStart.Enabled = true;

                // Show preview
                ShowPreview();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading file: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowPreview()
        {
            var previewTable = new System.Data.DataTable();
            previewTable.Columns.Add("First Name");
            previewTable.Columns.Add("Last Name");
            previewTable.Columns.Add("Model");
            previewTable.Columns.Add("Gender");
            previewTable.Columns.Add("License");
            previewTable.Columns.Add("Status");
            previewTable.Columns.Add("DOB");
            previewTable.Columns.Add("Wanted");

            int previewCount = Math.Min(100, _identities.Count);
            for (int i = 0; i < previewCount; i++)
            {
                var id = _identities[i];
                previewTable.Rows.Add(
                    id.FirstName ?? "",
                    id.LastName ?? "",
                    id.ModelName ?? "Unknown",
                    id.Gender == 1 ? "Female" : "Male",
                    id.LicenseNumber ?? "None",
                    id.HasValidLicense ? (id.LicenseSuspended ? "Suspended" : "Valid") : "No License",
                    id.DateOfBirth?.Substring(0, 10) ?? "Unknown",
                    id.IsWanted ? "Yes" : "No"
                );
            }

            gridPreview.DataSource = previewTable;
        }

        private void BtnStart_Click(object sender, EventArgs e)
        {
            if (_identities == null) return;

            if (!chkPreviewOnly.Checked)
            {
                var result = MessageBox.Show(
                    $"Import {_identities.Count} identities into database?\n\n" +
                    "This may take a few minutes.",
                    "Confirm Import",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result != DialogResult.Yes)
                    return;
            }

            // Disable controls during import
            btnStart.Enabled = false;
            btnBrowse.Enabled = false;
            btnLoad.Enabled = false;

            _currentIndex = 0;
            _imported = 0;
            _duplicates = 0;
            _skipped = 0;
            _errors = 0;

            // Start import in background
            System.Threading.Tasks.Task.Run(() => DoImport());
        }

        private void DoImport()
        {
            string[] maleModels = { "a_m_y_skater_01", "a_m_y_vinewood_01", "a_m_y_beach_01", "a_m_y_stlat_01", "g_m_y_famdnf_01" };
            string[] femaleModels = { "a_f_y_skater_01", "a_f_y_vinewood_01", "a_f_y_beach_01", "a_f_y_hipster_01", "a_f_y_tennis_01" };

            foreach (var identity in _identities)
            {
                try
                {
                    // Update progress
                    _currentIndex++;
                    int percent = (int)((double)_currentIndex / _identities.Count * 100);

                    this.Invoke((MethodInvoker)delegate
                    {
                        progressBar.Value = percent;
                        lblStatus.Text = $"Processing {_currentIndex}/{_identities.Count}: {identity.FirstName} {identity.LastName}";
                    });

                    // Skip if no name
                    if (string.IsNullOrEmpty(identity.FirstName) && string.IsNullOrEmpty(identity.LastName))
                    {
                        _skipped++;
                        continue;
                    }

                    string firstName = identity.FirstName ?? "";
                    string lastName = identity.LastName ?? "";

                    // Check for duplicates if enabled
                    if (chkSkipDuplicates.Checked)
                    {
                        var existing = LookupByName(firstName, lastName);
                        if (existing != null && existing.Count > 0)
                        {
                            _duplicates++;
                            continue;
                        }
                    }

                    if (chkPreviewOnly.Checked)
                    {
                        // Just counting for preview
                        _imported++;
                        continue;
                    }

                    // Determine model
                    string modelName = GetModelName(identity);

                    // Format DOB
                    string dob = ParseDateOfBirth(identity.DateOfBirth);

                    // Get license number
                    string licenseNumber = identity.LicenseNumber;
                    if (string.IsNullOrEmpty(licenseNumber))
                    {
                        licenseNumber = "ID" + _random.Next(100000, 999999).ToString();
                    }

                    // License status
                    string licenseStatus = "Valid";
                    if (identity.LicenseSuspended)
                        licenseStatus = "Suspended";
                    else if (!identity.HasValidLicense)
                        licenseStatus = "NoLicense";

                    // Insert into database
                    string insertQuery = @"
                        INSERT INTO peds (
                            first_name, last_name, model_name, gender, home_address,
                            has_home, home_type,
                            has_vehicle,
                            is_home_percent, is_driving_percent, in_world_percent, is_carrying_gun_percent,
                            license_number, license_status, license_reason, license_expiry, license_class,
                            date_of_birth,
                            is_wanted, wanted_reason,
                            is_incarcerated,
                            is_active
                        ) VALUES (
                            @firstName, @lastName, @modelName, @gender, @homeAddress,
                            0, 'None',
                            0,
                            @homePct, @drivePct, @worldPct, 10,
                            @licenseNumber, @licenseStatus, '', '2026-12-31', 'Class C',
                            @dob,
                            @isWanted, @wantedReason,
                            0,
                            1
                        )";

                    using (var cmd = new SQLiteCommand(insertQuery, _connection))
                    {
                        cmd.Parameters.AddWithValue("@firstName", firstName);
                        cmd.Parameters.AddWithValue("@lastName", lastName);
                        cmd.Parameters.AddWithValue("@modelName", modelName);
                        cmd.Parameters.AddWithValue("@gender", identity.Gender == 1 ? "Female" : "Male");
                        cmd.Parameters.AddWithValue("@homeAddress", GetHomeAddress(identity));
                        cmd.Parameters.AddWithValue("@homePct", (int)numDefaultHomePercent.Value);
                        cmd.Parameters.AddWithValue("@drivePct", (int)numDefaultDrivingPercent.Value);
                        cmd.Parameters.AddWithValue("@worldPct", (int)numDefaultWorldPercent.Value);
                        cmd.Parameters.AddWithValue("@licenseNumber", licenseNumber);
                        cmd.Parameters.AddWithValue("@licenseStatus", licenseStatus);
                        cmd.Parameters.AddWithValue("@dob", dob);
                        cmd.Parameters.AddWithValue("@isWanted", identity.IsWanted ? 1 : 0);
                        cmd.Parameters.AddWithValue("@wantedReason", identity.WantedReason ?? "");

                        cmd.ExecuteNonQuery();
                    }

                    _imported++;
                }
                catch (Exception ex)
                {
                    _errors++;
                    System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}");
                }
            }

            // Update UI when done
            this.Invoke((MethodInvoker)delegate
            {
                progressBar.Value = 100;
                lblStats.Text = $"Complete! Imported: {_imported}, Duplicates: {_duplicates}, Skipped: {_skipped}, Errors: {_errors}";
                lblStatus.Text = "Import finished";
                btnStart.Enabled = true;
                btnBrowse.Enabled = true;
                btnLoad.Enabled = true;

                if (!chkPreviewOnly.Checked)
                {
                    MessageBox.Show(
                        $"Import complete!\n\n" +
                        $"Total in file: {_identities.Count}\n" +
                        $"Imported: {_imported}\n" +
                        $"Duplicates: {_duplicates}\n" +
                        $"Skipped (no name): {_skipped}\n" +
                        $"Errors: {_errors}",
                        "Import Complete",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            });
        }

        private string GetModelName(IdentityJson identity)
        {
            switch (cmbDefaultModel.SelectedIndex)
            {
                case 0: // Use JSON model
                    if (!string.IsNullOrEmpty(identity.ModelName))
                        return identity.ModelName;
                    goto case 1; // Fall back to random

                case 1: // Random male
                    {
                        string[] maleModels = { "a_m_y_skater_01", "a_m_y_vinewood_01", "a_m_y_beach_01", "a_m_y_stlat_01" };
                        return maleModels[_random.Next(maleModels.Length)];
                    }

                case 2: // Random female
                    {
                        string[] femaleModels = { "a_f_y_skater_01", "a_f_y_vinewood_01", "a_f_y_beach_01", "a_f_y_hipster_01" };
                        return femaleModels[_random.Next(femaleModels.Length)];
                    }

                default: // Specific model selected
                    return cmbDefaultModel.SelectedItem.ToString();
            }
        }

        private string ParseDateOfBirth(string dob)
        {
            if (string.IsNullOrEmpty(dob)) return "";

            try
            {
                // Try to parse ISO format
                DateTime date = DateTime.Parse(dob);
                return date.ToString("yyyy-MM-dd");
            }
            catch
            {
                return dob;
            }
        }

        private string GetHomeAddress(IdentityJson identity)
        {
            if (identity.KnownLocations != null && identity.KnownLocations.Count > 0)
            {
                var loc = identity.KnownLocations[0];
                return $"({loc.X:F1}, {loc.Y:F1}, {loc.Z:F1})";
            }
            return "";
        }

        private List<Dictionary<string, object>> LookupByName(string firstName, string lastName)
        {
            var results = new List<Dictionary<string, object>>();

            string query = @"
                SELECT id, first_name, last_name 
                FROM peds 
                WHERE first_name = @firstName AND last_name = @lastName";

            using (var cmd = new SQLiteCommand(query, _connection))
            {
                cmd.Parameters.AddWithValue("@firstName", firstName);
                cmd.Parameters.AddWithValue("@lastName", lastName);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var person = new Dictionary<string, object>();
                        person["id"] = reader["id"];
                        person["first_name"] = reader["first_name"];
                        person["last_name"] = reader["last_name"];
                        results.Add(person);
                    }
                }
            }

            return results;
        }
    }

    // JSON classes
    public class IdentityJson
    {
        [JsonProperty("FirstName")]
        public string FirstName { get; set; }

        [JsonProperty("LastName")]
        public string LastName { get; set; }

        [JsonProperty("DateOfBirth")]
        public string DateOfBirth { get; set; }

        [JsonProperty("ModelName")]
        public string ModelName { get; set; }

        [JsonProperty("Gender")]
        public int Gender { get; set; }

        [JsonProperty("KnownLocations")]
        public List<LocationJson> KnownLocations { get; set; }

        [JsonProperty("LicenseNumber")]
        public string LicenseNumber { get; set; }

        [JsonProperty("HasValidLicense")]
        public bool HasValidLicense { get; set; }

        [JsonProperty("LicenseSuspended")]
        public bool LicenseSuspended { get; set; }

        [JsonProperty("IsWanted")]
        public bool IsWanted { get; set; }

        [JsonProperty("WantedReason")]
        public string WantedReason { get; set; }

        [JsonProperty("FullName")]
        public string FullName { get; set; }
    }

    public class LocationJson
    {
        [JsonProperty("X")]
        public float X { get; set; }

        [JsonProperty("Y")]
        public float Y { get; set; }

        [JsonProperty("Z")]
        public float Z { get; set; }
    }
}