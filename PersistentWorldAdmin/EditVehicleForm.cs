using System;
using System.Data.SQLite;
using System.Drawing;
using System.Windows.Forms;

namespace PersistentWorldAdmin
{
    public class EditVehicleForm : Form
    {
        private SQLiteConnection _connection;
        private int? _vehicleId;

        // Basic Info
        private TextBox txtPlate;
        private TextBox txtModel;
        private TextBox txtColor1;
        private TextBox txtColor2;
        private TextBox txtState;
        private ComboBox cmbOwnerType;
        private ComboBox cmbOwner;
        private TextBox txtNotes;

        // NEW FIELDS - Registration & Insurance
        private DateTimePicker dtpRegistrationExpiry;
        private CheckBox chkNoRegistration;
        private DateTimePicker dtpInsuranceExpiry;
        private CheckBox chkNoInsurance;

        // NEW FIELDS - Stolen Status
        private CheckBox chkIsStolen;
        private TextBox txtStolenReason;
        private DateTimePicker dtpStolenDate;
        private DateTimePicker dtpStolenRecoveredDate;
        private Button btnSetRecovered;

        // NEW FIELDS - Impounded Status
        private CheckBox chkIsImpounded;
        private TextBox txtImpoundedReason;
        private DateTimePicker dtpImpoundedDate;
        private TextBox txtImpoundedLocation;
        private Button btnReleaseImpound;

        private Button btnSave;
        private Button btnCancel;

        public EditVehicleForm(SQLiteConnection connection, int? vehicleId)
        {
            _connection = connection;
            _vehicleId = vehicleId;

            InitializeComponent();

            if (vehicleId.HasValue)
            {
                Text = "Edit Vehicle";
                LoadData();
            }
            else
            {
                Text = "Add New Vehicle";
                cmbOwnerType.SelectedIndex = 0;
                txtState.Text = "San Andreas";

                // Set default dates
                dtpRegistrationExpiry.Value = DateTime.Parse("2026-12-01");
                dtpInsuranceExpiry.Value = DateTime.Parse("2026-12-01");
                dtpStolenDate.Value = DateTime.Now;
                dtpStolenRecoveredDate.Value = DateTime.Now;
                dtpImpoundedDate.Value = DateTime.Now;

                // Default unchecked
                chkNoRegistration.Checked = false;
                chkNoInsurance.Checked = false;
                chkIsStolen.Checked = false;
                chkIsImpounded.Checked = false;

                UpdateStolenFields();
                UpdateImpoundedFields();
            }
        }

        private void InitializeComponent()
        {
            this.Size = new Size(700, 800);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.AutoScroll = true;

            int y = 20;
            int labelWidth = 120;
            int controlWidth = 250;
            int smallControlWidth = 150;

            // === BASIC INFO SECTION ===
            var lblBasic = new Label { Text = "BASIC INFORMATION", Location = new Point(20, y), Size = new Size(400, 20), Font = new Font("Arial", 10, FontStyle.Bold), ForeColor = Color.Blue };
            Controls.Add(lblBasic);
            y += 25;

            // License Plate
            AddLabel("Plate:*", 20, y, labelWidth);
            txtPlate = AddTextBox(150, y, controlWidth);
            txtPlate.CharacterCasing = CharacterCasing.Upper;
            y += 35;

            // Model
            AddLabel("Model:*", 20, y, labelWidth);
            txtModel = AddTextBox(150, y, controlWidth);
            y += 35;

            // Color 1
            AddLabel("Color 1:", 20, y, labelWidth);
            txtColor1 = AddTextBox(150, y, controlWidth);
            y += 35;

            // Color 2
            AddLabel("Color 2:", 20, y, labelWidth);
            txtColor2 = AddTextBox(150, y, controlWidth);
            y += 35;

            // State
            AddLabel("State:", 20, y, labelWidth);
            txtState = AddTextBox(150, y, controlWidth);
            y += 35;

            // Owner Type
            AddLabel("Owner Type:*", 20, y, labelWidth);
            cmbOwnerType = new ComboBox { Location = new Point(150, y), Size = new Size(controlWidth, 25), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbOwnerType.Items.AddRange(new[] { "person", "company" });
            cmbOwnerType.SelectedIndexChanged += (s, e) => LoadOwners();
            Controls.Add(cmbOwnerType);
            y += 35;

            // Owner
            AddLabel("Owner:*", 20, y, labelWidth);
            cmbOwner = new ComboBox { Location = new Point(150, y), Size = new Size(controlWidth, 25), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbOwner.DisplayMember = "Text";
            cmbOwner.ValueMember = "Value";
            Controls.Add(cmbOwner);
            y += 35;

            // Notes
            AddLabel("Notes:", 20, y, labelWidth);
            txtNotes = AddTextBox(150, y, controlWidth);
            y += 45;

            // === REGISTRATION & INSURANCE SECTION ===
            var lblRegInsurance = new Label { Text = "REGISTRATION & INSURANCE", Location = new Point(20, y), Size = new Size(400, 20), Font = new Font("Arial", 10, FontStyle.Bold), ForeColor = Color.Green };
            Controls.Add(lblRegInsurance);
            y += 25;

            // Registration Expiry
            AddLabel("Registration Expiry:", 20, y, labelWidth);
            dtpRegistrationExpiry = new DateTimePicker { Location = new Point(150, y), Size = new Size(smallControlWidth, 25), Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd" };
            Controls.Add(dtpRegistrationExpiry);

            chkNoRegistration = new CheckBox { Text = "No Registration", Location = new Point(320, y), Size = new Size(120, 25) };
            chkNoRegistration.CheckedChanged += (s, e) => dtpRegistrationExpiry.Enabled = !chkNoRegistration.Checked;
            Controls.Add(chkNoRegistration);
            y += 35;

            // Insurance Expiry
            AddLabel("Insurance Expiry:", 20, y, labelWidth);
            dtpInsuranceExpiry = new DateTimePicker { Location = new Point(150, y), Size = new Size(smallControlWidth, 25), Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd" };
            Controls.Add(dtpInsuranceExpiry);

            chkNoInsurance = new CheckBox { Text = "No Insurance", Location = new Point(320, y), Size = new Size(120, 25) };
            chkNoInsurance.CheckedChanged += (s, e) => dtpInsuranceExpiry.Enabled = !chkNoInsurance.Checked;
            Controls.Add(chkNoInsurance);
            y += 45;

            // === STOLEN STATUS SECTION ===
            var lblStolen = new Label { Text = "STOLEN STATUS", Location = new Point(20, y), Size = new Size(400, 20), Font = new Font("Arial", 10, FontStyle.Bold), ForeColor = Color.Red };
            Controls.Add(lblStolen);
            y += 25;

            // Is Stolen
            chkIsStolen = new CheckBox { Text = "Vehicle is Stolen", Location = new Point(150, y), Size = new Size(200, 25) };
            chkIsStolen.CheckedChanged += (s, e) => UpdateStolenFields();
            Controls.Add(chkIsStolen);
            y += 35;

            // Stolen Reason
            AddLabel("Stolen Reason:", 20, y, labelWidth);
            txtStolenReason = AddTextBox(150, y, controlWidth);
            y += 35;

            // Stolen Date
            AddLabel("Stolen Date:", 20, y, labelWidth);
            dtpStolenDate = new DateTimePicker { Location = new Point(150, y), Size = new Size(controlWidth, 25), Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss" };
            Controls.Add(dtpStolenDate);
            y += 35;

            // Recovered Date
            AddLabel("Recovered Date:", 20, y, labelWidth);
            dtpStolenRecoveredDate = new DateTimePicker { Location = new Point(150, y), Size = new Size(controlWidth, 25), Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss" };
            Controls.Add(dtpStolenRecoveredDate);

            btnSetRecovered = new Button { Text = "Mark Recovered", Location = new Point(410, y), Size = new Size(120, 25), BackColor = Color.LightGreen };
            btnSetRecovered.Click += BtnSetRecovered_Click;
            Controls.Add(btnSetRecovered);
            y += 45;

            // === IMPOUNDED STATUS SECTION ===
            var lblImpounded = new Label { Text = "IMPOUNDED STATUS", Location = new Point(20, y), Size = new Size(400, 20), Font = new Font("Arial", 10, FontStyle.Bold), ForeColor = Color.Orange };
            Controls.Add(lblImpounded);
            y += 25;

            // Is Impounded
            chkIsImpounded = new CheckBox { Text = "Vehicle is Impounded", Location = new Point(150, y), Size = new Size(200, 25) };
            chkIsImpounded.CheckedChanged += (s, e) => UpdateImpoundedFields();
            Controls.Add(chkIsImpounded);
            y += 35;

            // Impounded Reason
            AddLabel("Impound Reason:", 20, y, labelWidth);
            txtImpoundedReason = AddTextBox(150, y, controlWidth);
            y += 35;

            // Impounded Date
            AddLabel("Impound Date:", 20, y, labelWidth);
            dtpImpoundedDate = new DateTimePicker { Location = new Point(150, y), Size = new Size(controlWidth, 25), Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss" };
            Controls.Add(dtpImpoundedDate);
            y += 35;

            // Impounded Location
            AddLabel("Impound Location:", 20, y, labelWidth);
            txtImpoundedLocation = AddTextBox(150, y, controlWidth);
            y += 35;

            btnReleaseImpound = new Button { Text = "Release from Impound", Location = new Point(150, y), Size = new Size(180, 30), BackColor = Color.LightCoral };
            btnReleaseImpound.Click += BtnReleaseImpound_Click;
            y += 45;

            // Buttons
            btnSave = new Button { Text = "Save", Location = new Point(150, y), Size = new Size(100, 30), BackColor = Color.LightBlue };
            btnSave.Click += BtnSave_Click;

            btnCancel = new Button { Text = "Cancel", Location = new Point(260, y), Size = new Size(100, 30) };
            btnCancel.Click += (s, e) => DialogResult = DialogResult.Cancel;

            Controls.Add(btnSave);
            Controls.Add(btnCancel);

            // Help text
            var helpLabel = new Label
            {
                Text = "* Required fields",
                Location = new Point(150, y + 40),
                Size = new Size(300, 20),
                ForeColor = Color.Gray
            };
            Controls.Add(helpLabel);
        }

        private void UpdateStolenFields()
        {
            bool isStolen = chkIsStolen.Checked;
            txtStolenReason.Enabled = isStolen;
            dtpStolenDate.Enabled = isStolen;
            dtpStolenRecoveredDate.Enabled = isStolen;
            btnSetRecovered.Enabled = isStolen;

            // If not stolen, clear recovered date
            if (!isStolen)
            {
                dtpStolenRecoveredDate.Value = DateTime.Now;
            }
        }

        private void UpdateImpoundedFields()
        {
            bool isImpounded = chkIsImpounded.Checked;
            txtImpoundedReason.Enabled = isImpounded;
            dtpImpoundedDate.Enabled = isImpounded;
            txtImpoundedLocation.Enabled = isImpounded;
            btnReleaseImpound.Enabled = isImpounded;
        }

        private void BtnSetRecovered_Click(object sender, EventArgs e)
        {
            dtpStolenRecoveredDate.Value = DateTime.Now;
            chkIsStolen.Checked = false;
            MessageBox.Show("Vehicle marked as recovered. Save to update database.", "Recovered",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void BtnReleaseImpound_Click(object sender, EventArgs e)
        {
            chkIsImpounded.Checked = false;
            MessageBox.Show("Vehicle released from impound. Save to update database.", "Released",
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

        private void LoadOwners()
        {
            cmbOwner.Items.Clear();

            try
            {
                if (cmbOwnerType.SelectedItem?.ToString() == "person")
                {
                    using (var cmd = new SQLiteCommand("SELECT id, first_name || ' ' || last_name as name FROM peds ORDER BY last_name", _connection))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int id = Convert.ToInt32(reader["id"]);
                            string name = reader["name"].ToString();
                            cmbOwner.Items.Add(new { Text = name, Value = id });
                        }
                    }
                }
                else if (cmbOwnerType.SelectedItem?.ToString() == "company")
                {
                    using (var cmd = new SQLiteCommand("SELECT id, name FROM companies ORDER BY name", _connection))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int id = Convert.ToInt32(reader["id"]);
                            string name = reader["name"].ToString();
                            cmbOwner.Items.Add(new { Text = name, Value = id });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading owners: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadData()
        {
            try
            {
                string query = "SELECT * FROM vehicles WHERE id = @id";
                using (var cmd = new SQLiteCommand(query, _connection))
                {
                    cmd.Parameters.AddWithValue("@id", _vehicleId.Value);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            // Basic Info
                            txtPlate.Text = reader["license_plate"]?.ToString() ?? "";
                            txtModel.Text = reader["vehicle_model"]?.ToString() ?? "";
                            txtColor1.Text = reader["color_primary"]?.ToString() ?? "";
                            txtColor2.Text = reader["color_secondary"]?.ToString() ?? "";
                            txtState.Text = reader["registered_state"]?.ToString() ?? "San Andreas";
                            txtNotes.Text = reader["notes"]?.ToString() ?? "";

                            // Registration & Insurance
                            if (reader["registration_expiry"] != DBNull.Value)
                            {
                                DateTime regDate;
                                if (DateTime.TryParse(reader["registration_expiry"].ToString(), out regDate))
                                    dtpRegistrationExpiry.Value = regDate;
                            }

                            if (reader["insurance_expiry"] != DBNull.Value)
                            {
                                DateTime insDate;
                                if (DateTime.TryParse(reader["insurance_expiry"].ToString(), out insDate))
                                    dtpInsuranceExpiry.Value = insDate;
                            }

                            chkNoRegistration.Checked = reader["no_registration"] != DBNull.Value && Convert.ToInt32(reader["no_registration"]) == 1;
                            chkNoInsurance.Checked = reader["no_insurance"] != DBNull.Value && Convert.ToInt32(reader["no_insurance"]) == 1;

                            // Stolen Status
                            chkIsStolen.Checked = reader["is_stolen"] != DBNull.Value && Convert.ToInt32(reader["is_stolen"]) == 1;
                            txtStolenReason.Text = reader["stolen_reason"]?.ToString() ?? "";

                            if (reader["stolen_date"] != DBNull.Value)
                            {
                                DateTime stolenDate;
                                if (DateTime.TryParse(reader["stolen_date"].ToString(), out stolenDate))
                                    dtpStolenDate.Value = stolenDate;
                            }

                            if (reader["stolen_recovered_date"] != DBNull.Value)
                            {
                                DateTime recoveredDate;
                                if (DateTime.TryParse(reader["stolen_recovered_date"].ToString(), out recoveredDate))
                                    dtpStolenRecoveredDate.Value = recoveredDate;
                            }

                            // Impounded Status
                            chkIsImpounded.Checked = reader["is_impounded"] != DBNull.Value && Convert.ToInt32(reader["is_impounded"]) == 1;
                            txtImpoundedReason.Text = reader["impounded_reason"]?.ToString() ?? "";

                            if (reader["impounded_date"] != DBNull.Value)
                            {
                                DateTime impoundDate;
                                if (DateTime.TryParse(reader["impounded_date"].ToString(), out impoundDate))
                                    dtpImpoundedDate.Value = impoundDate;
                            }

                            txtImpoundedLocation.Text = reader["impounded_location"]?.ToString() ?? "";

                            // Owner Type
                            string ownerType = reader["owner_type"]?.ToString() ?? "person";
                            cmbOwnerType.SelectedItem = ownerType;

                            LoadOwners(); // Load owners after type is set

                            // Select the owner
                            if (reader["owner_id"] != DBNull.Value)
                            {
                                int ownerId = Convert.ToInt32(reader["owner_id"]);
                                bool ownerFound = false;

                                foreach (dynamic item in cmbOwner.Items)
                                {
                                    if (item.Value == ownerId)
                                    {
                                        cmbOwner.SelectedItem = item;
                                        ownerFound = true;
                                        break;
                                    }
                                }

                                if (!ownerFound)
                                {
                                    // Owner not found in list - might be deleted
                                    cmbOwner.Items.Add(new { Text = $"ID: {ownerId} (deleted)", Value = ownerId });
                                    cmbOwner.SelectedIndex = cmbOwner.Items.Count - 1;
                                }
                            }

                            // Update field states
                            UpdateStolenFields();
                            UpdateImpoundedFields();
                            dtpRegistrationExpiry.Enabled = !chkNoRegistration.Checked;
                            dtpInsuranceExpiry.Enabled = !chkNoInsurance.Checked;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading vehicle data: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private object GetValueOrNull(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return DBNull.Value;
            return value;
        }

        private object GetDateOrNull(DateTimePicker picker, bool useNull)
        {
            if (useNull)
                return DBNull.Value;
            return picker.Value.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            // Validate required fields
            if (string.IsNullOrWhiteSpace(txtPlate.Text))
            {
                MessageBox.Show("License Plate is required", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(txtModel.Text))
            {
                MessageBox.Show("Vehicle Model is required", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (cmbOwnerType.SelectedItem == null)
            {
                MessageBox.Show("Owner Type is required", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (cmbOwner.SelectedItem == null)
            {
                MessageBox.Show("Owner is required", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                dynamic owner = cmbOwner.SelectedItem;
                int ownerId = owner.Value;
                string ownerType = cmbOwnerType.SelectedItem.ToString();

                // Handle optional fields
                object color1 = GetValueOrNull(txtColor1.Text);
                object color2 = GetValueOrNull(txtColor2.Text);
                object state = GetValueOrNull(txtState.Text);
                object notes = GetValueOrNull(txtNotes.Text);

                // Registration & Insurance
                object registrationExpiry = chkNoRegistration.Checked ? DBNull.Value : (object)dtpRegistrationExpiry.Value.ToString("yyyy-MM-dd");
                object insuranceExpiry = chkNoInsurance.Checked ? DBNull.Value : (object)dtpInsuranceExpiry.Value.ToString("yyyy-MM-dd");
                int noRegistration = chkNoRegistration.Checked ? 1 : 0;
                int noInsurance = chkNoInsurance.Checked ? 1 : 0;

                // Stolen Status
                int isStolen = chkIsStolen.Checked ? 1 : 0;
                object stolenReason = chkIsStolen.Checked ? GetValueOrNull(txtStolenReason.Text) : DBNull.Value;
                object stolenDate = chkIsStolen.Checked ? (object)dtpStolenDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value;
                object stolenRecoveredDate = (!chkIsStolen.Checked && dtpStolenRecoveredDate.Value != DateTime.MinValue) ?
                    (object)dtpStolenRecoveredDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value;

                // Impounded Status
                int isImpounded = chkIsImpounded.Checked ? 1 : 0;
                object impoundedReason = chkIsImpounded.Checked ? GetValueOrNull(txtImpoundedReason.Text) : DBNull.Value;
                object impoundedDate = chkIsImpounded.Checked ? (object)dtpImpoundedDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value;
                object impoundedLocation = chkIsImpounded.Checked ? GetValueOrNull(txtImpoundedLocation.Text) : DBNull.Value;

                if (_vehicleId.HasValue)
                {
                    // Update
                    string sql = @"
                        UPDATE vehicles SET
                            license_plate = @plate,
                            vehicle_model = @model,
                            color_primary = @color1,
                            color_secondary = @color2,
                            registered_state = @state,
                            owner_type = @ownerType,
                            owner_id = @ownerId,
                            notes = @notes,
                            registration_expiry = @registrationExpiry,
                            insurance_expiry = @insuranceExpiry,
                            no_registration = @noRegistration,
                            no_insurance = @noInsurance,
                            is_stolen = @isStolen,
                            stolen_reason = @stolenReason,
                            stolen_date = @stolenDate,
                            stolen_recovered_date = @stolenRecoveredDate,
                            is_impounded = @isImpounded,
                            impounded_reason = @impoundedReason,
                            impounded_date = @impoundedDate,
                            impounded_location = @impoundedLocation,
                            last_modified = CURRENT_TIMESTAMP
                        WHERE id = @id";

                    using (var cmd = new SQLiteCommand(sql, _connection))
                    {
                        cmd.Parameters.AddWithValue("@id", _vehicleId.Value);
                        cmd.Parameters.AddWithValue("@plate", txtPlate.Text.ToUpper());
                        cmd.Parameters.AddWithValue("@model", txtModel.Text);
                        cmd.Parameters.AddWithValue("@color1", color1);
                        cmd.Parameters.AddWithValue("@color2", color2);
                        cmd.Parameters.AddWithValue("@state", state);
                        cmd.Parameters.AddWithValue("@ownerType", ownerType);
                        cmd.Parameters.AddWithValue("@ownerId", ownerId);
                        cmd.Parameters.AddWithValue("@notes", notes);

                        // New fields
                        cmd.Parameters.AddWithValue("@registrationExpiry", registrationExpiry);
                        cmd.Parameters.AddWithValue("@insuranceExpiry", insuranceExpiry);
                        cmd.Parameters.AddWithValue("@noRegistration", noRegistration);
                        cmd.Parameters.AddWithValue("@noInsurance", noInsurance);
                        cmd.Parameters.AddWithValue("@isStolen", isStolen);
                        cmd.Parameters.AddWithValue("@stolenReason", stolenReason);
                        cmd.Parameters.AddWithValue("@stolenDate", stolenDate);
                        cmd.Parameters.AddWithValue("@stolenRecoveredDate", stolenRecoveredDate);
                        cmd.Parameters.AddWithValue("@isImpounded", isImpounded);
                        cmd.Parameters.AddWithValue("@impoundedReason", impoundedReason);
                        cmd.Parameters.AddWithValue("@impoundedDate", impoundedDate);
                        cmd.Parameters.AddWithValue("@impoundedLocation", impoundedLocation);

                        cmd.ExecuteNonQuery();
                    }
                }
                else
                {
                    // Check if plate already exists
                    string checkSql = "SELECT COUNT(*) FROM vehicles WHERE license_plate = @plate";
                    using (var checkCmd = new SQLiteCommand(checkSql, _connection))
                    {
                        checkCmd.Parameters.AddWithValue("@plate", txtPlate.Text.ToUpper());
                        int count = Convert.ToInt32(checkCmd.ExecuteScalar());
                        if (count > 0)
                        {
                            MessageBox.Show($"A vehicle with plate {txtPlate.Text.ToUpper()} already exists!",
                                "Duplicate Plate", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                    }

                    // Insert
                    string sql = @"
                        INSERT INTO vehicles (
                            license_plate, vehicle_model, color_primary, color_secondary,
                            registered_state, owner_type, owner_id, notes,
                            registration_expiry, insurance_expiry, no_registration, no_insurance,
                            is_stolen, stolen_reason, stolen_date, stolen_recovered_date,
                            is_impounded, impounded_reason, impounded_date, impounded_location
                        ) VALUES (
                            @plate, @model, @color1, @color2, @state, @ownerType, @ownerId, @notes,
                            @registrationExpiry, @insuranceExpiry, @noRegistration, @noInsurance,
                            @isStolen, @stolenReason, @stolenDate, @stolenRecoveredDate,
                            @isImpounded, @impoundedReason, @impoundedDate, @impoundedLocation
                        )";

                    using (var cmd = new SQLiteCommand(sql, _connection))
                    {
                        cmd.Parameters.AddWithValue("@plate", txtPlate.Text.ToUpper());
                        cmd.Parameters.AddWithValue("@model", txtModel.Text);
                        cmd.Parameters.AddWithValue("@color1", color1);
                        cmd.Parameters.AddWithValue("@color2", color2);
                        cmd.Parameters.AddWithValue("@state", state);
                        cmd.Parameters.AddWithValue("@ownerType", ownerType);
                        cmd.Parameters.AddWithValue("@ownerId", ownerId);
                        cmd.Parameters.AddWithValue("@notes", notes);

                        // New fields
                        cmd.Parameters.AddWithValue("@registrationExpiry", registrationExpiry);
                        cmd.Parameters.AddWithValue("@insuranceExpiry", insuranceExpiry);
                        cmd.Parameters.AddWithValue("@noRegistration", noRegistration);
                        cmd.Parameters.AddWithValue("@noInsurance", noInsurance);
                        cmd.Parameters.AddWithValue("@isStolen", isStolen);
                        cmd.Parameters.AddWithValue("@stolenReason", stolenReason);
                        cmd.Parameters.AddWithValue("@stolenDate", stolenDate);
                        cmd.Parameters.AddWithValue("@stolenRecoveredDate", stolenRecoveredDate);
                        cmd.Parameters.AddWithValue("@isImpounded", isImpounded);
                        cmd.Parameters.AddWithValue("@impoundedReason", impoundedReason);
                        cmd.Parameters.AddWithValue("@impoundedDate", impoundedDate);
                        cmd.Parameters.AddWithValue("@impoundedLocation", impoundedLocation);

                        cmd.ExecuteNonQuery();
                    }
                }

                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving vehicle: {ex.Message}\n\nStack Trace: {ex.StackTrace}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}