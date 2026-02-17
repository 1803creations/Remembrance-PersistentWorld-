using System;
using System.Data.SQLite;
using System.Drawing;
using System.Windows.Forms;

namespace PersistentWorldAdmin
{
    public class EditCompanyForm : Form
    {
        private SQLiteConnection _connection;
        private int? _companyId;

        private TextBox txtName;
        private TextBox txtIndustry;
        private TextBox txtAddress;
        private TextBox txtPhone;

        private Button btnSave;
        private Button btnCancel;

        public EditCompanyForm(SQLiteConnection connection, int? companyId)
        {
            _connection = connection;
            _companyId = companyId;

            InitializeComponent();

            if (companyId.HasValue)
            {
                Text = "Edit Company";
                LoadData();
            }
            else
            {
                Text = "Add New Company";
            }
        }

        private void InitializeComponent()
        {
            this.Size = new Size(450, 250);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            int y = 20;
            int labelWidth = 100;
            int controlWidth = 250;

            // Name
            AddLabel("Name:*", 20, y, labelWidth);
            txtName = AddTextBox(130, y, controlWidth);
            y += 35;

            // Industry
            AddLabel("Industry:", 20, y, labelWidth);
            txtIndustry = AddTextBox(130, y, controlWidth);
            y += 35;

            // Address
            AddLabel("Address:", 20, y, labelWidth);
            txtAddress = AddTextBox(130, y, controlWidth);
            y += 35;

            // Phone
            AddLabel("Phone:", 20, y, labelWidth);
            txtPhone = AddTextBox(130, y, controlWidth);
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

        private void LoadData()
        {
            using (var cmd = new SQLiteCommand("SELECT * FROM companies WHERE id = @id", _connection))
            {
                cmd.Parameters.AddWithValue("@id", _companyId.Value);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        txtName.Text = reader["name"].ToString();
                        txtIndustry.Text = reader["industry"].ToString();
                        txtAddress.Text = reader["headquarters_address"].ToString();
                        txtPhone.Text = reader["phone_number"].ToString();
                    }
                }
            }
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                MessageBox.Show("Company name is required", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                if (_companyId.HasValue)
                {
                    // Update
                    string sql = "UPDATE companies SET name = @name, industry = @industry, headquarters_address = @address, phone_number = @phone WHERE id = @id";
                    using (var cmd = new SQLiteCommand(sql, _connection))
                    {
                        cmd.Parameters.AddWithValue("@id", _companyId.Value);
                        cmd.Parameters.AddWithValue("@name", txtName.Text);
                        cmd.Parameters.AddWithValue("@industry", txtIndustry.Text);
                        cmd.Parameters.AddWithValue("@address", txtAddress.Text);
                        cmd.Parameters.AddWithValue("@phone", txtPhone.Text);
                        cmd.ExecuteNonQuery();
                    }
                }
                else
                {
                    // Insert
                    string sql = "INSERT INTO companies (name, industry, headquarters_address, phone_number) VALUES (@name, @industry, @address, @phone)";
                    using (var cmd = new SQLiteCommand(sql, _connection))
                    {
                        cmd.Parameters.AddWithValue("@name", txtName.Text);
                        cmd.Parameters.AddWithValue("@industry", txtIndustry.Text);
                        cmd.Parameters.AddWithValue("@address", txtAddress.Text);
                        cmd.Parameters.AddWithValue("@phone", txtPhone.Text);
                        cmd.ExecuteNonQuery();
                    }
                }

                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving company: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}