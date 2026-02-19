using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PersistentWorldAdmin
{
    public partial class MainForm : Form
    {
        private SQLiteConnection _connection;
        private string _dbPath;

        // Controls
        private TabControl tabControl;
        private DataGridView peopleGrid;
        private DataGridView vehiclesGrid;
        private DataGridView ticketsGrid;
        private DataGridView companiesGrid;
        private DataGridView incarcerationHistoryGrid;
        private Button btnRefresh;
        private Button btnAddPerson;
        private Button btnEditPerson;
        private Button btnDeletePerson;
        private Button btnAddVehicle;
        private Button btnEditVehicle;
        private Button btnDeleteVehicle;
        private Button btnAddCompany;
        private Button btnEditCompany;
        private Button btnDeleteCompany;
        private Button btnAddTicket;
        private Button btnEditTicket;
        private Button btnDeleteTicket;
        private Button btnIncarcerate;
        private Button btnRelease;
        private Button btnSetWanted;
        private Button btnClearWanted;
        private Button btnImportJson;
        private Button btnMDT; // NEW MDT BUTTON
        private Label statusLabel;
        private TextBox txtSearch;

        public MainForm()
        {
            InitializeComponent();
            FindAndLoadDatabase();
        }

        private void InitializeComponent()
        {
            this.Text = "Persistent World Database Editor";
            this.Size = new Size(1400, 900);
            this.StartPosition = FormStartPosition.CenterScreen;

            // Menu Strip
            var menuStrip = new MenuStrip();
            var fileMenu = new ToolStripMenuItem("File");
            var exitItem = new ToolStripMenuItem("Exit", null, (s, e) => Application.Exit());
            var openItem = new ToolStripMenuItem("Open Database...", null, (s, e) => OpenDatabaseDialog());
            var refreshItem = new ToolStripMenuItem("Refresh", null, (s, e) => RefreshAll());
            var backupItem = new ToolStripMenuItem("Backup Database", null, (s, e) => BackupDatabase());

            // Add MDT to File menu
            var mdtMenuItem = new ToolStripMenuItem("Launch Police MDT", null, (s, e) => LaunchMDT());

            fileMenu.DropDownItems.Add(openItem);
            fileMenu.DropDownItems.Add(refreshItem);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(backupItem);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(mdtMenuItem); // MDT in File menu
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(exitItem);
            menuStrip.Items.Add(fileMenu);

            // Search Panel
            var searchPanel = new Panel();
            searchPanel.Height = 40;
            searchPanel.Dock = DockStyle.Top;
            searchPanel.BackColor = Color.White;
            searchPanel.Padding = new Padding(10, 5, 10, 5);

            var lblSearch = new Label { Text = "Search:", Location = new Point(10, 10), Size = new Size(50, 25) };
            txtSearch = new TextBox { Location = new Point(70, 10), Size = new Size(300, 25) };
            var btnSearch = new Button { Text = "Go", Location = new Point(380, 10), Size = new Size(60, 25) };
            btnSearch.Click += (s, e) => PerformSearch();

            searchPanel.Controls.Add(lblSearch);
            searchPanel.Controls.Add(txtSearch);
            searchPanel.Controls.Add(btnSearch);

            // Button Panel
            var buttonPanel = new Panel();
            buttonPanel.Height = 160;
            buttonPanel.Dock = DockStyle.Top;
            buttonPanel.BackColor = Color.LightGray;

            // Row 1 buttons - People
            var lblPeople = new Label { Text = "PEOPLE:", Location = new Point(10, 5), Size = new Size(100, 20), Font = new Font("Arial", 9, FontStyle.Bold) };
            buttonPanel.Controls.Add(lblPeople);

            btnRefresh = new Button { Text = "Refresh All", Location = new Point(10, 30), Size = new Size(100, 30) };
            btnRefresh.Click += (s, e) => RefreshAll();

            btnAddPerson = new Button { Text = "Add Person", Location = new Point(120, 30), Size = new Size(100, 30) };
            btnAddPerson.Click += BtnAddPerson_Click;

            btnEditPerson = new Button { Text = "Edit Person", Location = new Point(230, 30), Size = new Size(100, 30) };
            btnEditPerson.Click += BtnEditPerson_Click;

            btnDeletePerson = new Button { Text = "Delete Person", Location = new Point(340, 30), Size = new Size(100, 30) };
            btnDeletePerson.Click += BtnDeletePerson_Click;

            btnSetWanted = new Button { Text = "Set Wanted", Location = new Point(450, 30), Size = new Size(100, 30), BackColor = Color.LightCoral };
            btnSetWanted.Click += BtnSetWanted_Click;

            btnClearWanted = new Button { Text = "Clear Wanted", Location = new Point(560, 30), Size = new Size(100, 30), BackColor = Color.LightGreen };
            btnClearWanted.Click += BtnClearWanted_Click;

            btnIncarcerate = new Button { Text = "Incarcerate", Location = new Point(670, 30), Size = new Size(100, 30), BackColor = Color.LightCoral };
            btnIncarcerate.Click += BtnIncarcerate_Click;

            btnRelease = new Button { Text = "Release", Location = new Point(780, 30), Size = new Size(100, 30), BackColor = Color.LightGreen };
            btnRelease.Click += BtnRelease_Click;

            // Row 2 buttons - Vehicles
            var lblVehicles = new Label { Text = "VEHICLES:", Location = new Point(10, 65), Size = new Size(100, 20), Font = new Font("Arial", 9, FontStyle.Bold) };
            buttonPanel.Controls.Add(lblVehicles);

            btnAddVehicle = new Button { Text = "Add Vehicle", Location = new Point(120, 65), Size = new Size(100, 30) };
            btnAddVehicle.Click += BtnAddVehicle_Click;

            btnEditVehicle = new Button { Text = "Edit Vehicle", Location = new Point(230, 65), Size = new Size(100, 30) };
            btnEditVehicle.Click += BtnEditVehicle_Click;

            btnDeleteVehicle = new Button { Text = "Delete Vehicle", Location = new Point(340, 65), Size = new Size(100, 30) };
            btnDeleteVehicle.Click += BtnDeleteVehicle_Click;

            btnImportJson = new Button { Text = "Import JSON", Location = new Point(450, 65), Size = new Size(100, 30), BackColor = Color.LightBlue };
            btnImportJson.Click += BtnImportJson_Click;

            // NEW MDT BUTTON
            btnMDT = new Button { Text = "Police MDT", Location = new Point(560, 65), Size = new Size(100, 30), BackColor = Color.LightBlue };
            btnMDT.Click += BtnMDT_Click;

            // Row 3 buttons - Tickets
            var lblTickets = new Label { Text = "TICKETS:", Location = new Point(10, 100), Size = new Size(100, 20), Font = new Font("Arial", 9, FontStyle.Bold) };
            buttonPanel.Controls.Add(lblTickets);

            btnAddTicket = new Button { Text = "Add Ticket", Location = new Point(120, 100), Size = new Size(100, 30) };
            btnAddTicket.Click += BtnAddTicket_Click;

            btnEditTicket = new Button { Text = "Edit Ticket", Location = new Point(230, 100), Size = new Size(100, 30) };
            btnEditTicket.Click += BtnEditTicket_Click;

            btnDeleteTicket = new Button { Text = "Delete Ticket", Location = new Point(340, 100), Size = new Size(100, 30) };
            btnDeleteTicket.Click += BtnDeleteTicket_Click;

            // Row 4 buttons - Companies
            var lblCompanies = new Label { Text = "COMPANIES:", Location = new Point(450, 100), Size = new Size(100, 20), Font = new Font("Arial", 9, FontStyle.Bold) };
            buttonPanel.Controls.Add(lblCompanies);

            btnAddCompany = new Button { Text = "Add Company", Location = new Point(560, 100), Size = new Size(100, 30) };
            btnAddCompany.Click += BtnAddCompany_Click;

            btnEditCompany = new Button { Text = "Edit Company", Location = new Point(670, 100), Size = new Size(100, 30) };
            btnEditCompany.Click += BtnEditCompany_Click;

            btnDeleteCompany = new Button { Text = "Delete Company", Location = new Point(780, 100), Size = new Size(100, 30) };
            btnDeleteCompany.Click += BtnDeleteCompany_Click;

            buttonPanel.Controls.AddRange(new Control[] {
                btnRefresh, btnAddPerson, btnEditPerson, btnDeletePerson,
                btnSetWanted, btnClearWanted, btnIncarcerate, btnRelease,
                btnAddVehicle, btnEditVehicle, btnDeleteVehicle,
                btnImportJson, btnMDT, // ADDED MDT BUTTON HERE
                btnAddTicket, btnEditTicket, btnDeleteTicket,
                btnAddCompany, btnEditCompany, btnDeleteCompany
            });

            // Tab Control
            tabControl = new TabControl();
            tabControl.Dock = DockStyle.Fill;

            // People Tab
            peopleGrid = CreateGrid();
            peopleGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            peopleGrid.MultiSelect = false;
            peopleGrid.CellDoubleClick += (s, e) => BtnEditPerson_Click(s, e);
            tabControl.TabPages.Add(new TabPage("People") { Controls = { peopleGrid } });

            // Vehicles Tab
            vehiclesGrid = CreateGrid();
            vehiclesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            vehiclesGrid.MultiSelect = false;
            vehiclesGrid.CellDoubleClick += (s, e) => BtnEditVehicle_Click(s, e);
            tabControl.TabPages.Add(new TabPage("Vehicles") { Controls = { vehiclesGrid } });

            // Tickets Tab
            ticketsGrid = CreateGrid();
            ticketsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            ticketsGrid.MultiSelect = false;
            ticketsGrid.CellDoubleClick += (s, e) => BtnEditTicket_Click(s, e);
            tabControl.TabPages.Add(new TabPage("Tickets") { Controls = { ticketsGrid } });

            // Companies Tab
            companiesGrid = CreateGrid();
            companiesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            companiesGrid.MultiSelect = false;
            companiesGrid.CellDoubleClick += (s, e) => BtnEditCompany_Click(s, e);
            tabControl.TabPages.Add(new TabPage("Companies") { Controls = { companiesGrid } });

            // Incarceration History Tab
            incarcerationHistoryGrid = CreateGrid();
            incarcerationHistoryGrid.ReadOnly = true;
            tabControl.TabPages.Add(new TabPage("Incarceration History") { Controls = { incarcerationHistoryGrid } });

            // Status Label
            statusLabel = new Label();
            statusLabel.Dock = DockStyle.Bottom;
            statusLabel.Height = 25;
            statusLabel.BackColor = Color.White;
            statusLabel.Text = "Ready";
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.Padding = new Padding(5, 0, 0, 0);

            // Add controls to form
            this.Controls.Add(tabControl);
            this.Controls.Add(buttonPanel);
            this.Controls.Add(searchPanel);
            this.Controls.Add(menuStrip);
            this.Controls.Add(statusLabel);
            this.MainMenuStrip = menuStrip;
        }

        private DataGridView CreateGrid()
        {
            var grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.ReadOnly = true;
            grid.BackgroundColor = Color.White;
            grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            return grid;
        }

        private void FindAndLoadDatabase()
        {
            // Try multiple possible locations
            string[] possiblePaths = {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Plugins", "LSPDFR", "PersistentWorld", "PersistentWorld.db"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Plugins", "LSPDFR", "PersistentWorld", "PersistentWorld.db"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PersistentWorld", "PersistentWorld.db"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PersistentWorld.db"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "PersistentWorld.db")
            };

            statusLabel.Text = "Searching for database...";

            foreach (string path in possiblePaths)
            {
                string fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    _dbPath = fullPath;
                    break;
                }
            }

            if (_dbPath != null)
            {
                ConnectToDatabase(_dbPath);
            }
            else
            {
                statusLabel.Text = "Database not found. Use File > Open Database to locate it.";
                statusLabel.ForeColor = Color.Red;
            }
        }

        private void OpenDatabaseDialog()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "SQLite files (*.db)|*.db|All files (*.*)|*.*";
                dialog.Title = "Select Persistent World Database";
                dialog.InitialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Plugins", "LSPDFR", "PersistentWorld");

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    ConnectToDatabase(dialog.FileName);
                }
            }
        }

        private void ConnectToDatabase(string path)
        {
            try
            {
                _connection?.Close();

                _dbPath = path;
                _connection = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
                _connection.Open();

                RefreshAll();

                this.Text = $"Persistent World Editor - {Path.GetFileName(path)}";
                statusLabel.Text = $"Connected to: {path}";
                statusLabel.ForeColor = Color.Green;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error connecting to database: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Connection failed";
                statusLabel.ForeColor = Color.Red;
            }
        }

        private void BackupDatabase()
        {
            if (_connection == null) return;

            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "SQLite files (*.db)|*.db|All files (*.*)|*.*";
                dialog.DefaultExt = "db";
                dialog.FileName = $"PersistentWorld_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.db";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        _connection.Close();
                        File.Copy(_dbPath, dialog.FileName, true);
                        _connection.Open();

                        MessageBox.Show($"Database backed up successfully to:\n{dialog.FileName}", "Success",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error backing up database: {ex.Message}", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void PerformSearch()
        {
            if (_connection == null || string.IsNullOrWhiteSpace(txtSearch.Text))
            {
                RefreshAll();
                return;
            }

            string searchTerm = txtSearch.Text.Trim();

            // Search in people
            string peopleQuery = @"
                SELECT 
                    id,
                    first_name,
                    last_name,
                    model_name,
                    gender,
                    home_address,
                    license_number,
                    license_status,
                    license_reason,
                    license_expiry,
                    license_class,
                    date_of_birth,
                    is_wanted,
                    wanted_reason,
                    is_incarcerated,
                    incarcerated_reason,
                    incarcerated_days,
                    release_date,
                    is_active
                FROM peds 
                WHERE first_name LIKE @search OR last_name LIKE @search OR license_number LIKE @search
                ORDER BY last_name, first_name";

            var peopleTable = new System.Data.DataTable();
            using (var cmd = new SQLiteCommand(peopleQuery, _connection))
            {
                cmd.Parameters.AddWithValue("@search", "%" + searchTerm + "%");
                using (var reader = cmd.ExecuteReader())
                {
                    peopleTable.Load(reader);
                }
            }
            peopleGrid.DataSource = peopleTable;

            // Search in vehicles
            string vehicleQuery = @"
                SELECT 
                    v.id,
                    v.license_plate,
                    v.vehicle_model,
                    v.color_primary,
                    v.color_secondary,
                    v.registered_state,
                    v.owner_type,
                    v.registration_expiry,
                    v.insurance_expiry,
                    v.is_stolen,
                    v.is_impounded,
                    v.no_registration,
                    v.no_insurance,
                    CASE 
                        WHEN v.owner_type = 'person' THEN p.first_name || ' ' || p.last_name
                        WHEN v.owner_type = 'company' THEN c.name
                        ELSE 'Unknown'
                    END as owner_name,
                    v.notes
                FROM vehicles v
                LEFT JOIN peds p ON v.owner_type = 'person' AND v.owner_id = p.id
                LEFT JOIN companies c ON v.owner_type = 'company' AND v.owner_id = c.id
                WHERE v.license_plate LIKE @search OR v.vehicle_model LIKE @search
                ORDER BY v.license_plate";

            var vehicleTable = new System.Data.DataTable();
            using (var cmd = new SQLiteCommand(vehicleQuery, _connection))
            {
                cmd.Parameters.AddWithValue("@search", "%" + searchTerm + "%");
                using (var reader = cmd.ExecuteReader())
                {
                    vehicleTable.Load(reader);
                }
            }
            vehiclesGrid.DataSource = vehicleTable;

            statusLabel.Text = $"Search results for: {searchTerm}";
        }

        private void RefreshAll()
        {
            if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            {
                statusLabel.Text = "Not connected to database";
                return;
            }

            txtSearch.Clear();
            LoadPeople();
            LoadVehicles();
            LoadTickets();
            LoadCompanies();
            LoadIncarcerationHistory();
            statusLabel.Text = $"Data refreshed from: {_dbPath}";
        }

        private void LoadPeople()
        {
            string query = @"
                SELECT 
                    id,
                    first_name,
                    last_name,
                    model_name,
                    gender,
                    home_address,
                    license_number,
                    license_status,
                    license_reason,
                    license_expiry,
                    license_class,
                    date_of_birth,
                    is_wanted,
                    wanted_reason,
                    is_incarcerated,
                    incarcerated_reason,
                    incarcerated_days,
                    release_date,
                    is_active
                FROM peds
                ORDER BY last_name, first_name";

            var table = new System.Data.DataTable();
            using (var cmd = new SQLiteCommand(query, _connection))
            using (var reader = cmd.ExecuteReader())
            {
                table.Load(reader);
            }
            peopleGrid.DataSource = table;

            // Format columns
            if (peopleGrid.Columns.Count > 0)
            {
                peopleGrid.Columns["id"].Width = 40;
                peopleGrid.Columns["first_name"].Width = 80;
                peopleGrid.Columns["last_name"].Width = 80;
                peopleGrid.Columns["model_name"].Width = 100;
                peopleGrid.Columns["gender"].Width = 60;
                peopleGrid.Columns["home_address"].Width = 150;
                peopleGrid.Columns["license_number"].Width = 80;
                peopleGrid.Columns["license_status"].Width = 80;
                peopleGrid.Columns["license_reason"].Width = 120;
                peopleGrid.Columns["license_expiry"].Width = 80;
                peopleGrid.Columns["license_class"].Width = 70;
                peopleGrid.Columns["date_of_birth"].Width = 80;
                peopleGrid.Columns["is_wanted"].Width = 60;
                peopleGrid.Columns["wanted_reason"].Width = 120;
                peopleGrid.Columns["is_incarcerated"].Width = 80;
                peopleGrid.Columns["incarcerated_reason"].Width = 120;
                peopleGrid.Columns["incarcerated_days"].Width = 80;
                peopleGrid.Columns["release_date"].Width = 120;
                peopleGrid.Columns["is_active"].Width = 50;

                // Color code wanted/incarcerated rows
                peopleGrid.CellFormatting += (s, e) =>
                {
                    if (e.RowIndex >= 0 && peopleGrid.Rows[e.RowIndex].Cells["is_wanted"].Value != null)
                    {
                        bool isWanted = Convert.ToBoolean(peopleGrid.Rows[e.RowIndex].Cells["is_wanted"].Value);
                        if (isWanted)
                        {
                            e.CellStyle.BackColor = Color.LightCoral;
                            e.CellStyle.ForeColor = Color.Black;
                        }
                    }

                    if (e.RowIndex >= 0 && peopleGrid.Rows[e.RowIndex].Cells["is_incarcerated"].Value != null)
                    {
                        bool isIncarcerated = Convert.ToBoolean(peopleGrid.Rows[e.RowIndex].Cells["is_incarcerated"].Value);
                        if (isIncarcerated)
                        {
                            e.CellStyle.BackColor = Color.LightGray;
                            e.CellStyle.ForeColor = Color.Black;
                        }
                    }
                };
            }
        }

        private void LoadVehicles()
        {
            string query = @"
                SELECT 
                    v.id,
                    v.license_plate,
                    v.vehicle_model,
                    v.color_primary,
                    v.color_secondary,
                    v.registered_state,
                    v.owner_type,
                    v.owner_id,
                    v.registration_expiry,
                    v.insurance_expiry,
                    v.is_stolen,
                    v.stolen_reason,
                    v.stolen_date,
                    v.stolen_recovered_date,
                    v.is_impounded,
                    v.impounded_reason,
                    v.impounded_date,
                    v.impounded_location,
                    v.no_registration,
                    v.no_insurance,
                    CASE 
                        WHEN v.owner_type = 'person' THEN p.first_name || ' ' || p.last_name
                        WHEN v.owner_type = 'company' THEN c.name
                        ELSE 'Unknown'
                    END as owner_name,
                    p.is_wanted,
                    p.is_incarcerated,
                    v.notes,
                    v.created_date,
                    v.last_modified
                FROM vehicles v
                LEFT JOIN peds p ON v.owner_type = 'person' AND v.owner_id = p.id
                LEFT JOIN companies c ON v.owner_type = 'company' AND v.owner_id = c.id
                ORDER BY v.license_plate";

            var table = new System.Data.DataTable();
            using (var cmd = new SQLiteCommand(query, _connection))
            using (var reader = cmd.ExecuteReader())
            {
                table.Load(reader);
            }
            vehiclesGrid.DataSource = table;

            // Format columns
            if (vehiclesGrid.Columns.Count > 0)
            {
                // Hide some technical columns
                if (vehiclesGrid.Columns.Contains("id"))
                    vehiclesGrid.Columns["id"].Visible = false;
                if (vehiclesGrid.Columns.Contains("owner_id"))
                    vehiclesGrid.Columns["owner_id"].Visible = false;
                if (vehiclesGrid.Columns.Contains("stolen_reason"))
                    vehiclesGrid.Columns["stolen_reason"].Visible = false;
                if (vehiclesGrid.Columns.Contains("impounded_reason"))
                    vehiclesGrid.Columns["impounded_reason"].Visible = false;
                if (vehiclesGrid.Columns.Contains("stolen_date"))
                    vehiclesGrid.Columns["stolen_date"].Visible = false;
                if (vehiclesGrid.Columns.Contains("stolen_recovered_date"))
                    vehiclesGrid.Columns["stolen_recovered_date"].Visible = false;
                if (vehiclesGrid.Columns.Contains("impounded_date"))
                    vehiclesGrid.Columns["impounded_date"].Visible = false;
                if (vehiclesGrid.Columns.Contains("impounded_location"))
                    vehiclesGrid.Columns["impounded_location"].Visible = false;
                if (vehiclesGrid.Columns.Contains("created_date"))
                    vehiclesGrid.Columns["created_date"].Visible = false;
                if (vehiclesGrid.Columns.Contains("last_modified"))
                    vehiclesGrid.Columns["last_modified"].Visible = false;

                // Set column headers and widths
                if (vehiclesGrid.Columns.Contains("license_plate"))
                {
                    vehiclesGrid.Columns["license_plate"].HeaderText = "Plate";
                    vehiclesGrid.Columns["license_plate"].Width = 80;
                }

                if (vehiclesGrid.Columns.Contains("vehicle_model"))
                {
                    vehiclesGrid.Columns["vehicle_model"].HeaderText = "Model";
                    vehiclesGrid.Columns["vehicle_model"].Width = 100;
                }

                if (vehiclesGrid.Columns.Contains("color_primary"))
                {
                    vehiclesGrid.Columns["color_primary"].HeaderText = "Color 1";
                    vehiclesGrid.Columns["color_primary"].Width = 70;
                }

                if (vehiclesGrid.Columns.Contains("color_secondary"))
                {
                    vehiclesGrid.Columns["color_secondary"].HeaderText = "Color 2";
                    vehiclesGrid.Columns["color_secondary"].Width = 70;
                }

                if (vehiclesGrid.Columns.Contains("registered_state"))
                {
                    vehiclesGrid.Columns["registered_state"].HeaderText = "State";
                    vehiclesGrid.Columns["registered_state"].Width = 80;
                }

                if (vehiclesGrid.Columns.Contains("owner_type"))
                {
                    vehiclesGrid.Columns["owner_type"].HeaderText = "Type";
                    vehiclesGrid.Columns["owner_type"].Width = 70;
                }

                if (vehiclesGrid.Columns.Contains("owner_name"))
                {
                    vehiclesGrid.Columns["owner_name"].HeaderText = "Owner";
                    vehiclesGrid.Columns["owner_name"].Width = 150;
                }

                // New fields
                if (vehiclesGrid.Columns.Contains("registration_expiry"))
                {
                    vehiclesGrid.Columns["registration_expiry"].HeaderText = "Reg Expires";
                    vehiclesGrid.Columns["registration_expiry"].Width = 90;
                }

                if (vehiclesGrid.Columns.Contains("insurance_expiry"))
                {
                    vehiclesGrid.Columns["insurance_expiry"].HeaderText = "Ins Expires";
                    vehiclesGrid.Columns["insurance_expiry"].Width = 90;
                }

                if (vehiclesGrid.Columns.Contains("no_registration"))
                {
                    vehiclesGrid.Columns["no_registration"].HeaderText = "No Reg";
                    vehiclesGrid.Columns["no_registration"].Width = 60;
                }

                if (vehiclesGrid.Columns.Contains("no_insurance"))
                {
                    vehiclesGrid.Columns["no_insurance"].HeaderText = "No Ins";
                    vehiclesGrid.Columns["no_insurance"].Width = 60;
                }

                if (vehiclesGrid.Columns.Contains("is_stolen"))
                {
                    vehiclesGrid.Columns["is_stolen"].HeaderText = "Stolen";
                    vehiclesGrid.Columns["is_stolen"].Width = 60;
                }

                if (vehiclesGrid.Columns.Contains("is_impounded"))
                {
                    vehiclesGrid.Columns["is_impounded"].HeaderText = "Impounded";
                    vehiclesGrid.Columns["is_impounded"].Width = 80;
                }

                if (vehiclesGrid.Columns.Contains("is_wanted"))
                {
                    vehiclesGrid.Columns["is_wanted"].HeaderText = "Owner Wanted";
                    vehiclesGrid.Columns["is_wanted"].Width = 80;
                }

                if (vehiclesGrid.Columns.Contains("is_incarcerated"))
                {
                    vehiclesGrid.Columns["is_incarcerated"].HeaderText = "Owner Jailed";
                    vehiclesGrid.Columns["is_incarcerated"].Width = 80;
                }

                if (vehiclesGrid.Columns.Contains("notes"))
                {
                    vehiclesGrid.Columns["notes"].Width = 150;
                }

                // Color code rows based on status
                vehiclesGrid.CellFormatting += (s, e) =>
                {
                    if (e.RowIndex >= 0)
                    {
                        // Check if stolen
                        if (vehiclesGrid.Rows[e.RowIndex].Cells["is_stolen"].Value != null)
                        {
                            bool isStolen = Convert.ToInt32(vehiclesGrid.Rows[e.RowIndex].Cells["is_stolen"].Value) == 1;
                            if (isStolen)
                            {
                                e.CellStyle.BackColor = Color.LightCoral;
                                e.CellStyle.ForeColor = Color.Black;
                                return;
                            }
                        }

                        // Check if impounded
                        if (vehiclesGrid.Rows[e.RowIndex].Cells["is_impounded"].Value != null)
                        {
                            bool isImpounded = Convert.ToInt32(vehiclesGrid.Rows[e.RowIndex].Cells["is_impounded"].Value) == 1;
                            if (isImpounded)
                            {
                                e.CellStyle.BackColor = Color.LightYellow;
                                e.CellStyle.ForeColor = Color.Black;
                                return;
                            }
                        }

                        // Check if no registration
                        if (vehiclesGrid.Rows[e.RowIndex].Cells["no_registration"].Value != null)
                        {
                            bool noReg = Convert.ToInt32(vehiclesGrid.Rows[e.RowIndex].Cells["no_registration"].Value) == 1;
                            if (noReg)
                            {
                                e.CellStyle.BackColor = Color.LightGray;
                            }
                        }
                    }
                };
            }
        }

        private void LoadTickets()
        {
            string query = @"
                SELECT 
                    t.id,
                    p.first_name || ' ' || p.last_name as person_name,
                    p.id as person_id,
                    v.license_plate,
                    v.vehicle_model,
                    t.offense,
                    t.fine_amount,
                    t.date_issued,
                    t.issuing_officer,
                    t.location,
                    t.notes
                FROM tickets t
                JOIN peds p ON t.ped_id = p.id
                JOIN vehicles v ON t.vehicle_id = v.id
                ORDER BY t.date_issued DESC";

            var table = new System.Data.DataTable();
            using (var cmd = new SQLiteCommand(query, _connection))
            using (var reader = cmd.ExecuteReader())
            {
                table.Load(reader);
            }
            ticketsGrid.DataSource = table;

            // Format columns
            if (ticketsGrid.Columns.Count > 0)
            {
                ticketsGrid.Columns["id"].Width = 40;
                ticketsGrid.Columns["person_name"].Width = 150;
                ticketsGrid.Columns["person_id"].Width = 60;
                ticketsGrid.Columns["license_plate"].Width = 80;
                ticketsGrid.Columns["vehicle_model"].Width = 100;
                ticketsGrid.Columns["offense"].Width = 200;
                ticketsGrid.Columns["fine_amount"].Width = 60;
                ticketsGrid.Columns["date_issued"].Width = 120;
                ticketsGrid.Columns["issuing_officer"].Width = 100;
                ticketsGrid.Columns["location"].Width = 120;
                ticketsGrid.Columns["notes"].Width = 150;
            }
        }

        private void LoadCompanies()
        {
            string query = "SELECT id, name, industry, headquarters_address, phone_number FROM companies ORDER BY name";
            var table = new System.Data.DataTable();
            using (var cmd = new SQLiteCommand(query, _connection))
            using (var reader = cmd.ExecuteReader())
            {
                table.Load(reader);
            }
            companiesGrid.DataSource = table;
        }

        private void LoadIncarcerationHistory(int? pedId = null)
        {
            string query;
            if (pedId.HasValue)
            {
                query = @"
                    SELECT 
                        ih.id,
                        p.first_name || ' ' || p.last_name as person_name,
                        ih.reason,
                        ih.days_sentenced,
                        ih.date_incarcerated,
                        ih.date_released,
                        ih.released_by,
                        ih.notes
                    FROM incarceration_history ih
                    JOIN peds p ON ih.ped_id = p.id
                    WHERE ih.ped_id = @pedId
                    ORDER BY ih.date_incarcerated DESC";
            }
            else
            {
                query = @"
                    SELECT 
                        ih.id,
                        p.first_name || ' ' || p.last_name as person_name,
                        ih.reason,
                        ih.days_sentenced,
                        ih.date_incarcerated,
                        ih.date_released,
                        ih.released_by,
                        ih.notes
                    FROM incarceration_history ih
                    JOIN peds p ON ih.ped_id = p.id
                    ORDER BY ih.date_incarcerated DESC
                    LIMIT 100";
            }

            var table = new System.Data.DataTable();
            using (var cmd = new SQLiteCommand(query, _connection))
            {
                if (pedId.HasValue)
                    cmd.Parameters.AddWithValue("@pedId", pedId.Value);

                using (var reader = cmd.ExecuteReader())
                {
                    table.Load(reader);
                }
            }
            incarcerationHistoryGrid.DataSource = table;
        }

        // NEW METHOD: Launch External MDT
        private void LaunchMDT()
        {
            if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            {
                MessageBox.Show("Please open a database first", "No Database",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var mdtForm = new ExternalMdtForm();
            mdtForm.Show(); // Opens as separate window
        }

        private void BtnMDT_Click(object sender, EventArgs e)
        {
            LaunchMDT();
        }

        private void BtnAddPerson_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            var dialog = new EditPersonForm(_connection, null);
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                LoadPeople();
                statusLabel.Text = "Person added successfully";
            }
        }

        private void BtnEditPerson_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            if (peopleGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a person to edit", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int id = Convert.ToInt32(peopleGrid.SelectedRows[0].Cells["id"].Value);
            var dialog = new EditPersonForm(_connection, id);
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                LoadPeople();
                statusLabel.Text = "Person updated successfully";
            }
        }

        private void BtnDeletePerson_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            if (peopleGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a person to delete", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var row = peopleGrid.SelectedRows[0];
            string name = $"{row.Cells["first_name"].Value} {row.Cells["last_name"].Value}";
            int id = Convert.ToInt32(row.Cells["id"].Value);

            var result = MessageBox.Show($"Delete {name}?\nThis will also delete all associated vehicles and tickets!",
                "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                try
                {
                    using (var transaction = _connection.BeginTransaction())
                    {
                        // Get vehicle IDs to delete tickets
                        var vehicleCmd = new SQLiteCommand("SELECT id FROM vehicles WHERE owner_type = 'person' AND owner_id = @id", _connection);
                        vehicleCmd.Parameters.AddWithValue("@id", id);
                        var vehicles = new List<int>();
                        using (var reader = vehicleCmd.ExecuteReader())
                        {
                            while (reader.Read())
                                vehicles.Add(Convert.ToInt32(reader["id"]));
                        }

                        // Delete tickets for each vehicle
                        foreach (int vid in vehicles)
                        {
                            var ticketCmd = new SQLiteCommand("DELETE FROM tickets WHERE vehicle_id = @vid", _connection);
                            ticketCmd.Parameters.AddWithValue("@vid", vid);
                            ticketCmd.ExecuteNonQuery();
                        }

                        // Delete vehicles
                        var delVehCmd = new SQLiteCommand("DELETE FROM vehicles WHERE owner_type = 'person' AND owner_id = @id", _connection);
                        delVehCmd.Parameters.AddWithValue("@id", id);
                        delVehCmd.ExecuteNonQuery();

                        // Delete incarceration history
                        var histCmd = new SQLiteCommand("DELETE FROM incarceration_history WHERE ped_id = @id", _connection);
                        histCmd.Parameters.AddWithValue("@id", id);
                        histCmd.ExecuteNonQuery();

                        // Delete employment
                        var empCmd = new SQLiteCommand("DELETE FROM employment WHERE ped_id = @id", _connection);
                        empCmd.Parameters.AddWithValue("@id", id);
                        empCmd.ExecuteNonQuery();

                        // Delete person
                        var personCmd = new SQLiteCommand("DELETE FROM peds WHERE id = @id", _connection);
                        personCmd.Parameters.AddWithValue("@id", id);
                        personCmd.ExecuteNonQuery();

                        transaction.Commit();
                    }

                    LoadPeople();
                    LoadVehicles();
                    LoadTickets();
                    statusLabel.Text = "Person deleted successfully";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error deleting: {ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnSetWanted_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            if (peopleGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a person", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            BtnEditPerson_Click(sender, e);
        }

        private void BtnClearWanted_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            if (peopleGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a person", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int id = Convert.ToInt32(peopleGrid.SelectedRows[0].Cells["id"].Value);
            string name = $"{peopleGrid.SelectedRows[0].Cells["first_name"].Value} {peopleGrid.SelectedRows[0].Cells["last_name"].Value}";

            var result = MessageBox.Show($"Clear wanted status for {name}?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                try
                {
                    using (var cmd = new SQLiteCommand("UPDATE peds SET is_wanted = 0, wanted_reason = '' WHERE id = @id", _connection))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.ExecuteNonQuery();
                    }

                    LoadPeople();
                    statusLabel.Text = $"{name} wanted status cleared";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error clearing wanted: {ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnIncarcerate_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            if (peopleGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a person", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            BtnEditPerson_Click(sender, e);
        }

        private void BtnRelease_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            if (peopleGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a person", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int id = Convert.ToInt32(peopleGrid.SelectedRows[0].Cells["id"].Value);
            string name = $"{peopleGrid.SelectedRows[0].Cells["first_name"].Value} {peopleGrid.SelectedRows[0].Cells["last_name"].Value}";

            var result = MessageBox.Show($"Release {name} from incarceration?", "Confirm Release",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                try
                {
                    DateTime now = DateTime.Now;

                    using (var transaction = _connection.BeginTransaction())
                    {
                        // Update ped record
                        var updateCmd = new SQLiteCommand(@"
                            UPDATE peds SET 
                                is_incarcerated = 0,
                                release_date = @releaseDate
                            WHERE id = @id", _connection);

                        updateCmd.Parameters.AddWithValue("@id", id);
                        updateCmd.Parameters.AddWithValue("@releaseDate", now.ToString("yyyy-MM-dd HH:mm:ss"));
                        updateCmd.ExecuteNonQuery();

                        // Update history
                        var historyCmd = new SQLiteCommand(@"
                            UPDATE incarceration_history 
                            SET date_released = @releaseDate, released_by = 'Admin Tool'
                            WHERE ped_id = @id AND date_released IS NULL", _connection);

                        historyCmd.Parameters.AddWithValue("@id", id);
                        historyCmd.Parameters.AddWithValue("@releaseDate", now.ToString("yyyy-MM-dd HH:mm:ss"));
                        historyCmd.ExecuteNonQuery();

                        transaction.Commit();
                    }

                    LoadPeople();
                    LoadIncarcerationHistory(id);
                    statusLabel.Text = $"{name} released";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error releasing: {ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnAddVehicle_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            var dialog = new EditVehicleForm(_connection, null);
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                LoadVehicles();
                statusLabel.Text = "Vehicle added successfully";
            }
        }

        private void BtnEditVehicle_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            if (vehiclesGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a vehicle to edit", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int id = Convert.ToInt32(vehiclesGrid.SelectedRows[0].Cells["id"].Value);
            var dialog = new EditVehicleForm(_connection, id);
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                LoadVehicles();
                statusLabel.Text = "Vehicle updated successfully";
            }
        }

        private void BtnDeleteVehicle_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            if (vehiclesGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a vehicle to delete", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var row = vehiclesGrid.SelectedRows[0];
            string plate = row.Cells["license_plate"].Value.ToString();
            int id = Convert.ToInt32(row.Cells["id"].Value);

            var result = MessageBox.Show($"Delete vehicle with plate {plate}?\nThis will also delete all tickets for this vehicle!",
                "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                try
                {
                    using (var transaction = _connection.BeginTransaction())
                    {
                        // Delete tickets for this vehicle
                        var ticketCmd = new SQLiteCommand("DELETE FROM tickets WHERE vehicle_id = @id", _connection);
                        ticketCmd.Parameters.AddWithValue("@id", id);
                        ticketCmd.ExecuteNonQuery();

                        // Delete vehicle
                        var vehCmd = new SQLiteCommand("DELETE FROM vehicles WHERE id = @id", _connection);
                        vehCmd.Parameters.AddWithValue("@id", id);
                        vehCmd.ExecuteNonQuery();

                        transaction.Commit();
                    }

                    LoadVehicles();
                    LoadTickets();
                    statusLabel.Text = "Vehicle deleted successfully";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error deleting: {ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnAddTicket_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            var dialog = new EditTicketForm(_connection, null);
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                LoadTickets();
                statusLabel.Text = "Ticket added successfully";
            }
        }

        private void BtnEditTicket_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            if (ticketsGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a ticket to edit", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int id = Convert.ToInt32(ticketsGrid.SelectedRows[0].Cells["id"].Value);
            var dialog = new EditTicketForm(_connection, id);
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                LoadTickets();
                statusLabel.Text = "Ticket updated successfully";
            }
        }

        private void BtnDeleteTicket_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            if (ticketsGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a ticket to delete", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int id = Convert.ToInt32(ticketsGrid.SelectedRows[0].Cells["id"].Value);

            var result = MessageBox.Show("Delete this ticket?", "Confirm Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                try
                {
                    using (var cmd = new SQLiteCommand("DELETE FROM tickets WHERE id = @id", _connection))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.ExecuteNonQuery();
                    }

                    LoadTickets();
                    statusLabel.Text = "Ticket deleted successfully";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error deleting: {ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnAddCompany_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            var dialog = new EditCompanyForm(_connection, null);
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                LoadCompanies();
                statusLabel.Text = "Company added successfully";
            }
        }

        private void BtnEditCompany_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            if (companiesGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a company to edit", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int id = Convert.ToInt32(companiesGrid.SelectedRows[0].Cells["id"].Value);
            var dialog = new EditCompanyForm(_connection, id);
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                LoadCompanies();
                statusLabel.Text = "Company updated successfully";
            }
        }

        private void BtnDeleteCompany_Click(object sender, EventArgs e)
        {
            if (_connection == null) return;

            if (companiesGrid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a company to delete", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var row = companiesGrid.SelectedRows[0];
            string name = row.Cells["name"].Value.ToString();
            int id = Convert.ToInt32(row.Cells["id"].Value);

            var result = MessageBox.Show($"Delete company {name}?\nThis will also delete all fleet vehicles!",
                "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                try
                {
                    using (var transaction = _connection.BeginTransaction())
                    {
                        // Get vehicle IDs to delete tickets
                        var vehicleCmd = new SQLiteCommand("SELECT id FROM vehicles WHERE owner_type = 'company' AND owner_id = @id", _connection);
                        vehicleCmd.Parameters.AddWithValue("@id", id);
                        var vehicles = new List<int>();
                        using (var reader = vehicleCmd.ExecuteReader())
                        {
                            while (reader.Read())
                                vehicles.Add(Convert.ToInt32(reader["id"]));
                        }

                        // Delete tickets for each vehicle
                        foreach (int vid in vehicles)
                        {
                            var ticketCmd = new SQLiteCommand("DELETE FROM tickets WHERE vehicle_id = @vid", _connection);
                            ticketCmd.Parameters.AddWithValue("@vid", vid);
                            ticketCmd.ExecuteNonQuery();
                        }

                        // Delete vehicles
                        var delVehCmd = new SQLiteCommand("DELETE FROM vehicles WHERE owner_type = 'company' AND owner_id = @id", _connection);
                        delVehCmd.Parameters.AddWithValue("@id", id);
                        delVehCmd.ExecuteNonQuery();

                        // Delete employment records
                        var empCmd = new SQLiteCommand("DELETE FROM employment WHERE company_id = @id", _connection);
                        empCmd.Parameters.AddWithValue("@id", id);
                        empCmd.ExecuteNonQuery();

                        // Delete company
                        var compCmd = new SQLiteCommand("DELETE FROM companies WHERE id = @id", _connection);
                        compCmd.Parameters.AddWithValue("@id", id);
                        compCmd.ExecuteNonQuery();

                        transaction.Commit();
                    }

                    LoadCompanies();
                    LoadVehicles();
                    statusLabel.Text = "Company deleted successfully";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error deleting: {ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnImportJson_Click(object sender, EventArgs e)
        {
            if (_connection == null)
            {
                MessageBox.Show("Please connect to a database first", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var importForm = new ImportIdentitiesForm(_connection);
            importForm.ShowDialog();

            LoadPeople();
            statusLabel.Text = "JSON import completed - grid refreshed";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _connection?.Close();
            _connection?.Dispose();
            base.OnFormClosing(e);
        }
    }
}