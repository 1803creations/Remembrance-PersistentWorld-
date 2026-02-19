using System;
using System.Data.SQLite;
using System.Drawing;
using System.Windows.Forms;

namespace PersistentWorldAdmin
{
    public class EditPersonForm : Form
    {
        private SQLiteConnection _connection;
        private int? _personId;

        // Basic Info
        private TextBox txtFirstName;
        private TextBox txtLastName;
        private TextBox txtModelName;
        private ComboBox cmbGender;
        private TextBox txtHomeAddress;

        // Home Location
        private CheckBox chkHasHome;
        private ComboBox cmbHomeType;  // "None", "Interior", "Exterior"
        private NumericUpDown numHomeX;
        private NumericUpDown numHomeY;
        private NumericUpDown numHomeZ;
        private Button btnGetHomeCoords;

        // License Info
        private TextBox txtLicenseNumber;
        private ComboBox cmbLicenseStatus;
        private TextBox txtLicenseReason;
        private TextBox txtLicenseExpiry;
        private TextBox txtLicenseClass;
        private TextBox txtDateOfBirth;

        // Wanted/Incarcerated
        private CheckBox chkIsWanted;
        private TextBox txtWantedReason;
        private CheckBox chkIsIncarcerated;
        private TextBox txtIncarceratedReason;
        private NumericUpDown numIncarceratedDays;
        private DateTimePicker dtpReleaseDate;

        // Spawn Percentages
        private NumericUpDown numIsHomePercent;
        private NumericUpDown numIsDrivingPercent;
        private NumericUpDown numInWorldPercent;
        private NumericUpDown numIsCarryingGunPercent;

        // Status
        private CheckBox chkIsActive;

        private Button btnSave;
        private Button btnCancel;

        public EditPersonForm(SQLiteConnection connection, int? personId)
        {
            _connection = connection;
            _personId = personId;

            InitializeComponent();

            if (personId.HasValue)
            {
                Text = "Edit Person";
                LoadData();
            }
            else
            {
                Text = "Add New Person";
                chkIsActive.Checked = true;
                cmbLicenseStatus.SelectedIndex = 0;
                cmbGender.SelectedIndex = 0;
                dtpReleaseDate.Value = DateTime.Now.AddDays(30);

                // Set default spawn percentages
                numIsHomePercent.Value = 30;
                numIsDrivingPercent.Value = 30;
                numInWorldPercent.Value = 40;
                numIsCarryingGunPercent.Value = 10;

                // Default home settings
                cmbHomeType.SelectedIndex = 0; // "None"
                chkHasHome.Checked = false;

                UpdateHomeFields();
            }
        }

        private void InitializeComponent()
        {
            this.Size = new Size(900, 900);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.AutoScroll = true;

            int y = 20;
            int labelWidth = 140;
            int controlWidth = 300;

            // === BASIC INFO SECTION ===
            var lblBasic = new Label { Text = "BASIC INFORMATION", Location = new Point(20, y), Size = new Size(400, 20), Font = new Font("Arial", 10, FontStyle.Bold), ForeColor = Color.Blue };
            Controls.Add(lblBasic);
            y += 25;

            // First Name
            AddLabel("First Name:*", 20, y, labelWidth);
            txtFirstName = AddTextBox(170, y, controlWidth);
            y += 35;

            // Last Name
            AddLabel("Last Name:*", 20, y, labelWidth);
            txtLastName = AddTextBox(170, y, controlWidth);
            y += 35;

            // Model Name
            AddLabel("Model Name:", 20, y, labelWidth);
            txtModelName = AddTextBox(170, y, controlWidth);
            y += 35;

            // Gender
            AddLabel("Gender:", 20, y, labelWidth);
            cmbGender = new ComboBox { Location = new Point(170, y), Size = new Size(controlWidth, 25), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbGender.Items.AddRange(new[] { "Male", "Female", "Other" });
            Controls.Add(cmbGender);
            y += 35;

            // Home Address (text only)
            AddLabel("Home Address:", 20, y, labelWidth);
            txtHomeAddress = AddTextBox(170, y, controlWidth);
            y += 45;

            // === HOME LOCATION ===
            var lblHome = new Label { Text = "HOME LOCATION", Location = new Point(20, y), Size = new Size(400, 20), Font = new Font("Arial", 10, FontStyle.Bold), ForeColor = Color.Green };
            Controls.Add(lblHome);
            y += 25;

            // Has Home checkbox
            chkHasHome = new CheckBox { Text = "Has Home (spawns in world)", Location = new Point(170, y), Size = new Size(300, 25) };
            chkHasHome.CheckedChanged += (s, e) => UpdateHomeFields();
            Controls.Add(chkHasHome);
            y += 35;

            // Home Type (Interior/Exterior)
            AddLabel("Home Type:", 20, y, labelWidth);
            cmbHomeType = new ComboBox { Location = new Point(170, y), Size = new Size(150, 25), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbHomeType.Items.AddRange(new[] { "None", "Interior", "Exterior" });
            cmbHomeType.SelectedIndexChanged += (s, e) => UpdateHomeFields();
            cmbHomeType.Enabled = false;
            Controls.Add(cmbHomeType);
            y += 35;

            // Home Coordinates
            AddLabel("Home Coords:", 20, y, labelWidth);

            numHomeX = new NumericUpDown { Location = new Point(170, y), Size = new Size(90, 25), Minimum = -5000, Maximum = 5000, DecimalPlaces = 2, Increment = (decimal)0.1 };
            numHomeX.Enabled = false;
            Controls.Add(numHomeX);

            AddLabel("Y:", 270, y, 20);
            numHomeY = new NumericUpDown { Location = new Point(290, y), Size = new Size(90, 25), Minimum = -5000, Maximum = 5000, DecimalPlaces = 2, Increment = (decimal)0.1 };
            numHomeY.Enabled = false;
            Controls.Add(numHomeY);

            AddLabel("Z:", 390, y, 20);
            numHomeZ = new NumericUpDown { Location = new Point(410, y), Size = new Size(90, 25), Minimum = -500, Maximum = 5000, DecimalPlaces = 2, Increment = (decimal)0.1 };
            numHomeZ.Enabled = false;
            Controls.Add(numHomeZ);

            // Get Current Position button
            btnGetHomeCoords = new Button { Text = "Get Current", Location = new Point(510, y), Size = new Size(100, 25) };
            btnGetHomeCoords.Click += BtnGetHomeCoords_Click;
            btnGetHomeCoords.Enabled = false;
            Controls.Add(btnGetHomeCoords);
            y += 45;

            // === LICENSE SECTION ===
            var lblLicense = new Label { Text = "LICENSE INFORMATION", Location = new Point(20, y), Size = new Size(400, 20), Font = new Font("Arial", 10, FontStyle.Bold), ForeColor = Color.Blue };
            Controls.Add(lblLicense);
            y += 25;

            // License Number
            AddLabel("License #:", 20, y, labelWidth);
            txtLicenseNumber = AddTextBox(170, y, controlWidth);
            y += 35;

            // License Status
            AddLabel("License Status:", 20, y, labelWidth);
            cmbLicenseStatus = new ComboBox { Location = new Point(170, y), Size = new Size(controlWidth, 25), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbLicenseStatus.Items.AddRange(new[] { "Valid", "Suspended", "Revoked", "Expired", "NoLicense" });
            Controls.Add(cmbLicenseStatus);
            y += 35;

            // License Reason
            AddLabel("License Reason:", 20, y, labelWidth);
            txtLicenseReason = AddTextBox(170, y, controlWidth);
            y += 35;

            // License Expiry
            AddLabel("License Expiry:", 20, y, labelWidth);
            txtLicenseExpiry = AddTextBox(170, y, controlWidth);
            y += 35;

            // License Class
            AddLabel("License Class:", 20, y, labelWidth);
            txtLicenseClass = AddTextBox(170, y, controlWidth);
            y += 35;

            // Date of Birth
            AddLabel("Date of Birth:", 20, y, labelWidth);
            txtDateOfBirth = AddTextBox(170, y, controlWidth);
            y += 45;

            // === WANTED SECTION ===
            var lblWanted = new Label { Text = "WANTED STATUS", Location = new Point(20, y), Size = new Size(400, 20), Font = new Font("Arial", 10, FontStyle.Bold), ForeColor = Color.Red };
            Controls.Add(lblWanted);
            y += 25;

            // Is Wanted
            chkIsWanted = new CheckBox { Text = "Is Wanted", Location = new Point(170, y), Size = new Size(controlWidth, 25) };
            chkIsWanted.CheckedChanged += (s, e) => txtWantedReason.Enabled = chkIsWanted.Checked;
            Controls.Add(chkIsWanted);
            y += 35;

            // Wanted Reason
            AddLabel("Wanted Reason:", 20, y, labelWidth);
            txtWantedReason = AddTextBox(170, y, controlWidth);
            txtWantedReason.Enabled = false;
            y += 45;

            // === INCARCERATION SECTION ===
            var lblIncarcerated = new Label { Text = "INCARCERATION STATUS", Location = new Point(20, y), Size = new Size(400, 20), Font = new Font("Arial", 10, FontStyle.Bold), ForeColor = Color.Red };
            Controls.Add(lblIncarcerated);
            y += 25;

            // Is Incarcerated
            chkIsIncarcerated = new CheckBox { Text = "Is Incarcerated", Location = new Point(170, y), Size = new Size(controlWidth, 25) };
            chkIsIncarcerated.CheckedChanged += (s, e) => {
                txtIncarceratedReason.Enabled = chkIsIncarcerated.Checked;
                numIncarceratedDays.Enabled = chkIsIncarcerated.Checked;
                dtpReleaseDate.Enabled = chkIsIncarcerated.Checked;
            };
            Controls.Add(chkIsIncarcerated);
            y += 35;

            // Incarcerated Reason
            AddLabel("Incarceration Reason:", 20, y, labelWidth);
            txtIncarceratedReason = AddTextBox(170, y, controlWidth);
            txtIncarceratedReason.Enabled = false;
            y += 35;

            // Incarcerated Days
            AddLabel("Sentence (days):", 20, y, labelWidth);
            numIncarceratedDays = new NumericUpDown { Location = new Point(170, y), Size = new Size(100, 25), Minimum = 0, Maximum = 9999, Value = 30 };
            numIncarceratedDays.Enabled = false;
            Controls.Add(numIncarceratedDays);
            y += 35;

            // Release Date
            AddLabel("Release Date:", 20, y, labelWidth);
            dtpReleaseDate = new DateTimePicker { Location = new Point(170, y), Size = new Size(controlWidth, 25), Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss" };
            dtpReleaseDate.Enabled = false;
            Controls.Add(dtpReleaseDate);
            y += 45;

            // === SPAWN PERCENTAGES SECTION ===
            var lblSpawn = new Label { Text = "SPAWN PERCENTAGES", Location = new Point(20, y), Size = new Size(400, 20), Font = new Font("Arial", 10, FontStyle.Bold), ForeColor = Color.Green };
            Controls.Add(lblSpawn);
            y += 25;

            // IsHomePercent
            AddLabel("Home %:", 20, y, labelWidth);
            numIsHomePercent = new NumericUpDown { Location = new Point(170, y), Size = new Size(80, 25), Minimum = 0, Maximum = 100, Value = 30 };
            Controls.Add(numIsHomePercent);
            y += 35;

            // IsDrivingPercent
            AddLabel("Driving %:", 20, y, labelWidth);
            numIsDrivingPercent = new NumericUpDown { Location = new Point(170, y), Size = new Size(80, 25), Minimum = 0, Maximum = 100, Value = 30 };
            Controls.Add(numIsDrivingPercent);
            y += 35;

            // InWorldPercent
            AddLabel("World %:", 20, y, labelWidth);
            numInWorldPercent = new NumericUpDown { Location = new Point(170, y), Size = new Size(80, 25), Minimum = 0, Maximum = 100, Value = 40 };
            Controls.Add(numInWorldPercent);
            y += 35;

            // IsCarryingGunPercent
            AddLabel("Gun %:", 20, y, labelWidth);
            numIsCarryingGunPercent = new NumericUpDown { Location = new Point(170, y), Size = new Size(80, 25), Minimum = 0, Maximum = 100, Value = 10 };
            Controls.Add(numIsCarryingGunPercent);
            y += 35;

            // Percentage note
            var percentNote = new Label
            {
                Text = "Note: These control spawn behavior. They don't need to add to 100.",
                Location = new Point(170, y),
                Size = new Size(400, 20),
                ForeColor = Color.Gray,
                Font = new Font("Arial", 8)
            };
            Controls.Add(percentNote);
            y += 30;

            // Is Active
            chkIsActive = new CheckBox { Text = "Is Active", Location = new Point(170, y), Size = new Size(controlWidth, 25), Checked = true };
            Controls.Add(chkIsActive);
            y += 45;

            // Buttons
            btnSave = new Button { Text = "Save", Location = new Point(170, y), Size = new Size(100, 30) };
            btnSave.Click += BtnSave_Click;

            btnCancel = new Button { Text = "Cancel", Location = new Point(280, y), Size = new Size(100, 30) };
            btnCancel.Click += (s, e) => DialogResult = DialogResult.Cancel;

            Controls.Add(btnSave);
            Controls.Add(btnCancel);

            // Help text
            var helpLabel = new Label
            {
                Text = "* Required fields (First and Last Name only)",
                Location = new Point(170, y + 40),
                Size = new Size(300, 20),
                ForeColor = Color.Gray
            };
            Controls.Add(helpLabel);
        }

        private void UpdateHomeFields()
        {
            bool hasHome = chkHasHome.Checked;
            cmbHomeType.Enabled = hasHome;

            // Only enable coordinates if home type is not "None"
            bool hasCoordinates = hasHome && cmbHomeType.SelectedIndex > 0; // 0 = None
            numHomeX.Enabled = hasCoordinates;
            numHomeY.Enabled = hasCoordinates;
            numHomeZ.Enabled = hasCoordinates;
            btnGetHomeCoords.Enabled = hasCoordinates;
        }

        private void BtnGetHomeCoords_Click(object sender, EventArgs e)
        {
            // This would need to interface with GTA V to get current player position
            MessageBox.Show("This would get the current player position from GTA V", "Get Coordinates",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void AddLabel(string text, int x, int y, int width)
        {
            Controls.Add(new Label { Text = text, Location = new Point(x, y), Size = new Size(width, 25) });
        }

        private TextBox AddTextBox(int x, int y, int width)
        {
            var tb = new TextBox { Location = new Point(x, y), Size = new Size(width, 25) };
            Controls.Add(tb);
            return tb;
        }

        private void LoadData()
        {
            try
            {
                using (var cmd = new SQLiteCommand("SELECT * FROM peds WHERE id = @id", _connection))
                {
                    cmd.Parameters.AddWithValue("@id", _personId.Value);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            // Basic Info
                            txtFirstName.Text = reader["first_name"]?.ToString() ?? "";
                            txtLastName.Text = reader["last_name"]?.ToString() ?? "";
                            txtModelName.Text = reader["model_name"]?.ToString() ?? "";
                            cmbGender.SelectedItem = reader["gender"]?.ToString() ?? "Male";
                            txtHomeAddress.Text = reader["home_address"]?.ToString() ?? "";

                            // Home Location
                            try
                            {
                                chkHasHome.Checked = reader["has_home"] != DBNull.Value ? Convert.ToInt32(reader["has_home"]) == 1 : false;
                                cmbHomeType.SelectedItem = reader["home_type"]?.ToString() ?? "None";

                                if (reader["home_coord_x"] != DBNull.Value)
                                    numHomeX.Value = Convert.ToDecimal(reader["home_coord_x"]);
                                if (reader["home_coord_y"] != DBNull.Value)
                                    numHomeY.Value = Convert.ToDecimal(reader["home_coord_y"]);
                                if (reader["home_coord_z"] != DBNull.Value)
                                    numHomeZ.Value = Convert.ToDecimal(reader["home_coord_z"]);
                            }
                            catch
                            {
                                // Columns don't exist yet - use defaults
                                chkHasHome.Checked = false;
                                cmbHomeType.SelectedIndex = 0;
                            }

                            // License Info
                            txtLicenseNumber.Text = reader["license_number"]?.ToString() ?? "";
                            cmbLicenseStatus.SelectedItem = reader["license_status"]?.ToString() ?? "Valid";
                            txtLicenseReason.Text = reader["license_reason"]?.ToString() ?? "";
                            txtLicenseExpiry.Text = reader["license_expiry"]?.ToString() ?? "";
                            txtLicenseClass.Text = reader["license_class"]?.ToString() ?? "Class C";
                            txtDateOfBirth.Text = reader["date_of_birth"]?.ToString() ?? "";

                            // Wanted fields
                            if (reader["is_wanted"] != DBNull.Value)
                                chkIsWanted.Checked = Convert.ToInt32(reader["is_wanted"]) == 1;
                            txtWantedReason.Text = reader["wanted_reason"]?.ToString() ?? "";
                            txtWantedReason.Enabled = chkIsWanted.Checked;

                            // Incarcerated fields
                            if (reader["is_incarcerated"] != DBNull.Value)
                                chkIsIncarcerated.Checked = Convert.ToInt32(reader["is_incarcerated"]) == 1;
                            txtIncarceratedReason.Text = reader["incarcerated_reason"]?.ToString() ?? "";

                            if (reader["incarcerated_days"] != DBNull.Value)
                                numIncarceratedDays.Value = Convert.ToInt32(reader["incarcerated_days"]);

                            // Handle release date
                            if (reader["release_date"] != DBNull.Value && !string.IsNullOrEmpty(reader["release_date"].ToString()))
                            {
                                string releaseDateStr = reader["release_date"].ToString();
                                try
                                {
                                    DateTime releaseDate;
                                    if (DateTime.TryParse(releaseDateStr, out releaseDate))
                                    {
                                        dtpReleaseDate.Value = releaseDate;
                                    }
                                }
                                catch { /* Use default if parse fails */ }
                            }

                            txtIncarceratedReason.Enabled = chkIsIncarcerated.Checked;
                            numIncarceratedDays.Enabled = chkIsIncarcerated.Checked;
                            dtpReleaseDate.Enabled = chkIsIncarcerated.Checked;

                            // Spawn Percentages
                            try
                            {
                                if (reader["is_home_percent"] != DBNull.Value)
                                    numIsHomePercent.Value = Convert.ToInt32(reader["is_home_percent"]);
                                if (reader["is_driving_percent"] != DBNull.Value)
                                    numIsDrivingPercent.Value = Convert.ToInt32(reader["is_driving_percent"]);
                                if (reader["in_world_percent"] != DBNull.Value)
                                    numInWorldPercent.Value = Convert.ToInt32(reader["in_world_percent"]);
                                if (reader["is_carrying_gun_percent"] != DBNull.Value)
                                    numIsCarryingGunPercent.Value = Convert.ToInt32(reader["is_carrying_gun_percent"]);
                            }
                            catch
                            {
                                // Use defaults
                                numIsHomePercent.Value = 30;
                                numIsDrivingPercent.Value = 30;
                                numInWorldPercent.Value = 40;
                                numIsCarryingGunPercent.Value = 10;
                            }

                            if (reader["is_active"] != DBNull.Value)
                                chkIsActive.Checked = Convert.ToInt32(reader["is_active"]) == 1;

                            // Update UI states
                            UpdateHomeFields();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading data: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private object GetValueOrNull(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return DBNull.Value;
            return value;
        }

        private object GetDoubleOrNull(decimal? value)
        {
            if (value.HasValue)
                return (double)value.Value;
            return DBNull.Value;
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            // Only validate first and last name
            if (string.IsNullOrWhiteSpace(txtFirstName.Text) || string.IsNullOrWhiteSpace(txtLastName.Text))
            {
                MessageBox.Show("First and Last name are required", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                // Handle NULL values for text fields
                object modelName = GetValueOrNull(txtModelName.Text);
                object gender = GetValueOrNull(cmbGender.SelectedItem?.ToString());
                object homeAddress = GetValueOrNull(txtHomeAddress.Text);
                object licenseNumber = GetValueOrNull(txtLicenseNumber.Text);
                object licenseStatus = GetValueOrNull(cmbLicenseStatus.SelectedItem?.ToString());
                object licenseReason = GetValueOrNull(txtLicenseReason.Text);
                object licenseExpiry = GetValueOrNull(txtLicenseExpiry.Text);
                object licenseClass = GetValueOrNull(txtLicenseClass.Text);
                object dateOfBirth = GetValueOrNull(txtDateOfBirth.Text);
                object wantedReason = chkIsWanted.Checked ? GetValueOrNull(txtWantedReason.Text) : DBNull.Value;
                object incarceratedReason = chkIsIncarcerated.Checked ? GetValueOrNull(txtIncarceratedReason.Text) : DBNull.Value;
                object incarceratedDays = chkIsIncarcerated.Checked ? (int)numIncarceratedDays.Value : (object)DBNull.Value;
                object releaseDate = chkIsIncarcerated.Checked ? dtpReleaseDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : (object)DBNull.Value;

                // Home location values
                int hasHome = chkHasHome.Checked ? 1 : 0;
                string homeType = cmbHomeType.SelectedItem?.ToString() ?? "None";

                object homeX = DBNull.Value;
                object homeY = DBNull.Value;
                object homeZ = DBNull.Value;

                if (chkHasHome.Checked && cmbHomeType.SelectedIndex > 0)
                {
                    homeX = GetDoubleOrNull((decimal?)numHomeX.Value);
                    homeY = GetDoubleOrNull((decimal?)numHomeY.Value);
                    homeZ = GetDoubleOrNull((decimal?)numHomeZ.Value);
                }

                // Spawn percentages
                int isHomePercent = (int)numIsHomePercent.Value;
                int isDrivingPercent = (int)numIsDrivingPercent.Value;
                int inWorldPercent = (int)numInWorldPercent.Value;
                int isCarryingGunPercent = (int)numIsCarryingGunPercent.Value;

                if (_personId.HasValue)
                {
                    // Update - REMOVED ALL VEHICLE FIELDS
                    string sql = @"
                        UPDATE peds SET
                            first_name = @firstName,
                            last_name = @lastName,
                            model_name = @modelName,
                            gender = @gender,
                            home_address = @homeAddress,
                            
                            -- Home location
                            has_home = @hasHome,
                            home_type = @homeType,
                            home_coord_x = @homeX,
                            home_coord_y = @homeY,
                            home_coord_z = @homeZ,
                            
                            -- Spawn percentages
                            is_home_percent = @isHomePercent,
                            is_driving_percent = @isDrivingPercent,
                            in_world_percent = @inWorldPercent,
                            is_carrying_gun_percent = @isCarryingGunPercent,
                            
                            license_number = @licenseNumber,
                            license_status = @licenseStatus,
                            license_reason = @licenseReason,
                            license_expiry = @licenseExpiry,
                            license_class = @licenseClass,
                            date_of_birth = @dateOfBirth,
                            is_wanted = @isWanted,
                            wanted_reason = @wantedReason,
                            is_incarcerated = @isIncarcerated,
                            incarcerated_reason = @incarceratedReason,
                            incarcerated_days = @incarceratedDays,
                            release_date = @releaseDate,
                            is_active = @isActive
                        WHERE id = @id";

                    using (var cmd = new SQLiteCommand(sql, _connection))
                    {
                        cmd.Parameters.AddWithValue("@id", _personId.Value);
                        cmd.Parameters.AddWithValue("@firstName", txtFirstName.Text);
                        cmd.Parameters.AddWithValue("@lastName", txtLastName.Text);
                        cmd.Parameters.AddWithValue("@modelName", modelName);
                        cmd.Parameters.AddWithValue("@gender", gender);
                        cmd.Parameters.AddWithValue("@homeAddress", homeAddress);

                        // Home params
                        cmd.Parameters.AddWithValue("@hasHome", hasHome);
                        cmd.Parameters.AddWithValue("@homeType", homeType);
                        cmd.Parameters.AddWithValue("@homeX", homeX);
                        cmd.Parameters.AddWithValue("@homeY", homeY);
                        cmd.Parameters.AddWithValue("@homeZ", homeZ);

                        // Spawn percent params
                        cmd.Parameters.AddWithValue("@isHomePercent", isHomePercent);
                        cmd.Parameters.AddWithValue("@isDrivingPercent", isDrivingPercent);
                        cmd.Parameters.AddWithValue("@inWorldPercent", inWorldPercent);
                        cmd.Parameters.AddWithValue("@isCarryingGunPercent", isCarryingGunPercent);

                        cmd.Parameters.AddWithValue("@licenseNumber", licenseNumber);
                        cmd.Parameters.AddWithValue("@licenseStatus", licenseStatus);
                        cmd.Parameters.AddWithValue("@licenseReason", licenseReason);
                        cmd.Parameters.AddWithValue("@licenseExpiry", licenseExpiry);
                        cmd.Parameters.AddWithValue("@licenseClass", licenseClass);
                        cmd.Parameters.AddWithValue("@dateOfBirth", dateOfBirth);
                        cmd.Parameters.AddWithValue("@isWanted", chkIsWanted.Checked ? 1 : 0);
                        cmd.Parameters.AddWithValue("@wantedReason", wantedReason);
                        cmd.Parameters.AddWithValue("@isIncarcerated", chkIsIncarcerated.Checked ? 1 : 0);
                        cmd.Parameters.AddWithValue("@incarceratedReason", incarceratedReason);
                        cmd.Parameters.AddWithValue("@incarceratedDays", incarceratedDays);
                        cmd.Parameters.AddWithValue("@releaseDate", releaseDate);
                        cmd.Parameters.AddWithValue("@isActive", chkIsActive.Checked ? 1 : 0);

                        cmd.ExecuteNonQuery();
                    }

                    // If newly incarcerated, add to history
                    if (chkIsIncarcerated.Checked && _personId.HasValue && !string.IsNullOrWhiteSpace(txtIncarceratedReason.Text))
                    {
                        // Check if already has an open incarceration
                        string checkSql = "SELECT COUNT(*) FROM incarceration_history WHERE ped_id = @id AND date_released IS NULL";
                        int openCount = 0;
                        using (var checkCmd = new SQLiteCommand(checkSql, _connection))
                        {
                            checkCmd.Parameters.AddWithValue("@id", _personId.Value);
                            openCount = Convert.ToInt32(checkCmd.ExecuteScalar());
                        }

                        if (openCount == 0)
                        {
                            string historySql = @"
                                INSERT INTO incarceration_history (ped_id, reason, days_sentenced, date_incarcerated, notes)
                                VALUES (@id, @reason, @days, @date, @notes)";

                            using (var cmd = new SQLiteCommand(historySql, _connection))
                            {
                                cmd.Parameters.AddWithValue("@id", _personId.Value);
                                cmd.Parameters.AddWithValue("@reason", txtIncarceratedReason.Text);
                                cmd.Parameters.AddWithValue("@days", (int)numIncarceratedDays.Value);
                                cmd.Parameters.AddWithValue("@date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                                cmd.Parameters.AddWithValue("@notes", "Updated in Admin Tool");
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    MessageBox.Show("Person saved successfully!",
                        "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    // Insert - REMOVED ALL VEHICLE FIELDS
                    string sql = @"
                        INSERT INTO peds (
                            first_name, last_name, model_name, gender, home_address,
                            has_home, home_type, home_coord_x, home_coord_y, home_coord_z,
                            is_home_percent, is_driving_percent, in_world_percent, is_carrying_gun_percent,
                            license_number, license_status, license_reason, license_expiry,
                            license_class, date_of_birth, is_wanted, wanted_reason,
                            is_incarcerated, incarcerated_reason, incarcerated_days,
                            release_date, is_active
                        ) VALUES (
                            @firstName, @lastName, @modelName, @gender, @homeAddress,
                            @hasHome, @homeType, @homeX, @homeY, @homeZ,
                            @isHomePercent, @isDrivingPercent, @inWorldPercent, @isCarryingGunPercent,
                            @licenseNumber, @licenseStatus, @licenseReason, @licenseExpiry,
                            @licenseClass, @dateOfBirth, @isWanted, @wantedReason,
                            @isIncarcerated, @incarceratedReason, @incarceratedDays,
                            @releaseDate, @isActive
                        )";

                    using (var cmd = new SQLiteCommand(sql, _connection))
                    {
                        cmd.Parameters.AddWithValue("@firstName", txtFirstName.Text);
                        cmd.Parameters.AddWithValue("@lastName", txtLastName.Text);
                        cmd.Parameters.AddWithValue("@modelName", modelName);
                        cmd.Parameters.AddWithValue("@gender", gender);
                        cmd.Parameters.AddWithValue("@homeAddress", homeAddress);

                        // Home params
                        cmd.Parameters.AddWithValue("@hasHome", hasHome);
                        cmd.Parameters.AddWithValue("@homeType", homeType);
                        cmd.Parameters.AddWithValue("@homeX", homeX);
                        cmd.Parameters.AddWithValue("@homeY", homeY);
                        cmd.Parameters.AddWithValue("@homeZ", homeZ);

                        // Spawn percent params
                        cmd.Parameters.AddWithValue("@isHomePercent", isHomePercent);
                        cmd.Parameters.AddWithValue("@isDrivingPercent", isDrivingPercent);
                        cmd.Parameters.AddWithValue("@inWorldPercent", inWorldPercent);
                        cmd.Parameters.AddWithValue("@isCarryingGunPercent", isCarryingGunPercent);

                        cmd.Parameters.AddWithValue("@licenseNumber", licenseNumber);
                        cmd.Parameters.AddWithValue("@licenseStatus", licenseStatus);
                        cmd.Parameters.AddWithValue("@licenseReason", licenseReason);
                        cmd.Parameters.AddWithValue("@licenseExpiry", licenseExpiry);
                        cmd.Parameters.AddWithValue("@licenseClass", licenseClass);
                        cmd.Parameters.AddWithValue("@dateOfBirth", dateOfBirth);
                        cmd.Parameters.AddWithValue("@isWanted", chkIsWanted.Checked ? 1 : 0);
                        cmd.Parameters.AddWithValue("@wantedReason", wantedReason);
                        cmd.Parameters.AddWithValue("@isIncarcerated", chkIsIncarcerated.Checked ? 1 : 0);
                        cmd.Parameters.AddWithValue("@incarceratedReason", incarceratedReason);
                        cmd.Parameters.AddWithValue("@incarceratedDays", incarceratedDays);
                        cmd.Parameters.AddWithValue("@releaseDate", releaseDate);
                        cmd.Parameters.AddWithValue("@isActive", chkIsActive.Checked ? 1 : 0);

                        cmd.ExecuteNonQuery();
                    }

                    // If incarcerated, add to history
                    if (chkIsIncarcerated.Checked && !string.IsNullOrWhiteSpace(txtIncarceratedReason.Text))
                    {
                        long newId = _connection.LastInsertRowId;
                        string historySql = @"
                            INSERT INTO incarceration_history (ped_id, reason, days_sentenced, date_incarcerated, notes)
                            VALUES (@id, @reason, @days, @date, @notes)";

                        using (var cmd = new SQLiteCommand(historySql, _connection))
                        {
                            cmd.Parameters.AddWithValue("@id", (int)newId);
                            cmd.Parameters.AddWithValue("@reason", txtIncarceratedReason.Text);
                            cmd.Parameters.AddWithValue("@days", (int)numIncarceratedDays.Value);
                            cmd.Parameters.AddWithValue("@date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                            cmd.Parameters.AddWithValue("@notes", "Created in Admin Tool");
                            cmd.ExecuteNonQuery();
                        }
                    }

                    MessageBox.Show("Person added successfully!",
                        "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving: {ex.Message}\n\nStack Trace: {ex.StackTrace}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}