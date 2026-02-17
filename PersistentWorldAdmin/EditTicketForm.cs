using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PersistentWorldAdmin
{
    public class EditTicketForm : Form
    {
        private SQLiteConnection _connection;
        private int? _ticketId;

        private ComboBox cmbPerson;
        private ComboBox cmbVehicle;
        private TextBox txtOffense;
        private NumericUpDown numFine;
        private DateTimePicker dtpDate;
        private TextBox txtOfficer;
        private TextBox txtLocation;
        private TextBox txtNotes;

        private Button btnSave;
        private Button btnCancel;

        private List<dynamic> _people = new List<dynamic>();
        private List<dynamic> _vehicles = new List<dynamic>();

        public EditTicketForm(SQLiteConnection connection, int? ticketId)
        {
            _connection = connection;
            _ticketId = ticketId;

            InitializeComponent();
            LoadPeople();
            LoadAllVehicles(); // Load ALL vehicles, not just person's

            if (ticketId.HasValue)
            {
                Text = "Edit Ticket";
                LoadData();
            }
            else
            {
                Text = "Add New Ticket";
                dtpDate.Value = DateTime.Now;
            }
        }

        private void InitializeComponent()
        {
            this.Size = new Size(500, 450);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            int y = 20;
            int labelWidth = 100;
            int controlWidth = 300;

            // Person
            AddLabel("Person:*", 20, y, labelWidth);
            cmbPerson = new ComboBox { Location = new Point(130, y), Size = new Size(controlWidth, 25), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbPerson.DisplayMember = "Text";
            cmbPerson.ValueMember = "Value";
            Controls.Add(cmbPerson);
            y += 35;

            // Vehicle
            AddLabel("Vehicle:*", 20, y, labelWidth);
            cmbVehicle = new ComboBox { Location = new Point(130, y), Size = new Size(controlWidth, 25), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbVehicle.DisplayMember = "Text";
            cmbVehicle.ValueMember = "Value";
            Controls.Add(cmbVehicle);
            y += 35;

            // Offense
            AddLabel("Offense:*", 20, y, labelWidth);
            txtOffense = AddTextBox(130, y, controlWidth);
            y += 35;

            // Fine Amount
            AddLabel("Fine Amount:*", 20, y, labelWidth);
            numFine = new NumericUpDown { Location = new Point(130, y), Size = new Size(100, 25), Minimum = 0, Maximum = 10000, Value = 100 };
            Controls.Add(numFine);
            y += 35;

            // Date Issued
            AddLabel("Date Issued:", 20, y, labelWidth);
            dtpDate = new DateTimePicker { Location = new Point(130, y), Size = new Size(controlWidth, 25), Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss" };
            Controls.Add(dtpDate);
            y += 35;

            // Issuing Officer
            AddLabel("Officer:", 20, y, labelWidth);
            txtOfficer = AddTextBox(130, y, controlWidth);
            y += 35;

            // Location
            AddLabel("Location:", 20, y, labelWidth);
            txtLocation = AddTextBox(130, y, controlWidth);
            y += 35;

            // Notes
            AddLabel("Notes:", 20, y, labelWidth);
            txtNotes = AddTextBox(130, y, controlWidth);
            y += 45;

            // Buttons
            btnSave = new Button { Text = "Save", Location = new Point(130, y), Size = new Size(100, 30) };
            btnSave.Click += BtnSave_Click;

            btnCancel = new Button { Text = "Cancel", Location = new Point(240, y), Size = new Size(100, 30) };
            btnCancel.Click += (s, e) => DialogResult = DialogResult.Cancel;

            Controls.Add(btnSave);
            Controls.Add(btnCancel);

            // Help text
            var helpLabel = new Label
            {
                Text = "* Required fields",
                Location = new Point(130, y + 40),
                Size = new Size(300, 20),
                ForeColor = Color.Gray
            };
            Controls.Add(helpLabel);
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

        private void LoadPeople()
        {
            cmbPerson.Items.Clear();
            _people.Clear();

            try
            {
                using (var cmd = new SQLiteCommand("SELECT id, first_name || ' ' || last_name as name FROM peds ORDER BY last_name", _connection))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int id = Convert.ToInt32(reader["id"]);
                        string name = reader["name"].ToString();
                        var item = new { Text = name, Value = id };
                        _people.Add(item);
                        cmbPerson.Items.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading people: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadAllVehicles()
        {
            cmbVehicle.Items.Clear();
            _vehicles.Clear();

            try
            {
                using (var cmd = new SQLiteCommand(@"
                    SELECT v.id, v.license_plate, v.vehicle_model, 
                           CASE 
                               WHEN v.owner_type = 'person' THEN p.first_name || ' ' || p.last_name
                               WHEN v.owner_type = 'company' THEN c.name
                               ELSE 'Unknown'
                           END as owner_name
                    FROM vehicles v
                    LEFT JOIN peds p ON v.owner_type = 'person' AND v.owner_id = p.id
                    LEFT JOIN companies c ON v.owner_type = 'company' AND v.owner_id = c.id
                    ORDER BY v.license_plate", _connection))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int id = Convert.ToInt32(reader["id"]);
                        string plate = reader["license_plate"].ToString();
                        string model = reader["vehicle_model"].ToString();
                        string owner = reader["owner_name"]?.ToString() ?? "Unknown";
                        var item = new { Text = $"{plate} ({model}) - {owner}", Value = id, Plate = plate };
                        _vehicles.Add(item);
                        cmbVehicle.Items.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading vehicles: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadData()
        {
            try
            {
                string query = @"
                    SELECT t.*, p.first_name || ' ' || p.last_name as person_name, v.license_plate
                    FROM tickets t
                    JOIN peds p ON t.ped_id = p.id
                    JOIN vehicles v ON t.vehicle_id = v.id
                    WHERE t.id = @id";

                using (var cmd = new SQLiteCommand(query, _connection))
                {
                    cmd.Parameters.AddWithValue("@id", _ticketId.Value);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            // Select person
                            int personId = Convert.ToInt32(reader["ped_id"]);
                            foreach (dynamic item in _people)
                            {
                                if (item.Value == personId)
                                {
                                    cmbPerson.SelectedItem = item;
                                    break;
                                }
                            }

                            // Select vehicle
                            int vehicleId = Convert.ToInt32(reader["vehicle_id"]);
                            foreach (dynamic item in cmbVehicle.Items)
                            {
                                if (item.Value == vehicleId)
                                {
                                    cmbVehicle.SelectedItem = item;
                                    break;
                                }
                            }

                            txtOffense.Text = reader["offense"].ToString();
                            numFine.Value = Convert.ToInt32(reader["fine_amount"]);

                            DateTime date;
                            if (DateTime.TryParse(reader["date_issued"].ToString(), out date))
                                dtpDate.Value = date;

                            txtOfficer.Text = reader["issuing_officer"].ToString();
                            txtLocation.Text = reader["location"].ToString();
                            txtNotes.Text = reader["notes"].ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading ticket data: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private object GetValueOrNull(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return DBNull.Value;
            return value;
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            // Validate required fields
            if (cmbPerson.SelectedItem == null)
            {
                MessageBox.Show("Please select a person", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (cmbVehicle.SelectedItem == null)
            {
                MessageBox.Show("Please select a vehicle", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(txtOffense.Text))
            {
                MessageBox.Show("Offense is required", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                dynamic person = cmbPerson.SelectedItem;
                int personId = person.Value;

                dynamic vehicle = cmbVehicle.SelectedItem;
                int vehicleId = vehicle.Value;

                // Handle optional fields
                object officer = GetValueOrNull(txtOfficer.Text);
                object location = GetValueOrNull(txtLocation.Text);
                object notes = GetValueOrNull(txtNotes.Text);

                if (_ticketId.HasValue)
                {
                    // Update
                    string sql = @"
                        UPDATE tickets SET
                            ped_id = @pedId,
                            vehicle_id = @vehicleId,
                            offense = @offense,
                            fine_amount = @fine,
                            date_issued = @date,
                            issuing_officer = @officer,
                            location = @location,
                            notes = @notes
                        WHERE id = @id";

                    using (var cmd = new SQLiteCommand(sql, _connection))
                    {
                        cmd.Parameters.AddWithValue("@id", _ticketId.Value);
                        cmd.Parameters.AddWithValue("@pedId", personId);
                        cmd.Parameters.AddWithValue("@vehicleId", vehicleId);
                        cmd.Parameters.AddWithValue("@offense", txtOffense.Text);
                        cmd.Parameters.AddWithValue("@fine", (int)numFine.Value);
                        cmd.Parameters.AddWithValue("@date", dtpDate.Value.ToString("yyyy-MM-dd HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@officer", officer);
                        cmd.Parameters.AddWithValue("@location", location);
                        cmd.Parameters.AddWithValue("@notes", notes);

                        cmd.ExecuteNonQuery();
                    }
                }
                else
                {
                    // Insert
                    string sql = @"
                        INSERT INTO tickets (
                            ped_id, vehicle_id, offense, fine_amount, date_issued,
                            issuing_officer, location, notes
                        ) VALUES (
                            @pedId, @vehicleId, @offense, @fine, @date,
                            @officer, @location, @notes
                        )";

                    using (var cmd = new SQLiteCommand(sql, _connection))
                    {
                        cmd.Parameters.AddWithValue("@pedId", personId);
                        cmd.Parameters.AddWithValue("@vehicleId", vehicleId);
                        cmd.Parameters.AddWithValue("@offense", txtOffense.Text);
                        cmd.Parameters.AddWithValue("@fine", (int)numFine.Value);
                        cmd.Parameters.AddWithValue("@date", dtpDate.Value.ToString("yyyy-MM-dd HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@officer", officer);
                        cmd.Parameters.AddWithValue("@location", location);
                        cmd.Parameters.AddWithValue("@notes", notes);

                        cmd.ExecuteNonQuery();
                    }
                }

                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving ticket: {ex.Message}\n\nStack Trace: {ex.StackTrace}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}