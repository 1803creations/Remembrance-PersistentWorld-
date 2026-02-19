using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PersistentWorldAdmin
{
    public partial class ExternalMdtForm : Form
    {
        private SQLiteConnection _connection;
        private string _dbPath;

        // Search mode
        private enum SearchMode { Vehicle, Person }
        private SearchMode _currentSearchMode = SearchMode.Vehicle;

        // Input fields
        private TextBox txtVehiclePlate;
        private TextBox txtPersonFirstName;
        private TextBox txtPersonLastName;

        // Results display
        private ListBox lstResults;
        private RichTextBox txtDetails;
        private DataGridView gridTickets;
        private Label lblPersonName;
        private Label lblWantedStatus;
        private Label lblIncarceratedStatus;

        // Selected person/vehicle data
        private Dictionary<string, object> _currentVehicle = null;
        private Dictionary<string, object> _currentPerson = null;
        private List<Dictionary<string, object>> _personResults = null;

        // Ticket creation
        private Button btnIssueTicket;

        // Suggestion controls
        private ListBox lstSuggestions;
        private Timer suggestionTimer;

        // Menu items
        private ToolStripMenuItem openToolStripMenuItem;
        private ToolStripMenuItem exitToolStripMenuItem;
        private ToolStripMenuItem refreshToolStripMenuItem;

        // Status strip
        private StatusStrip statusStrip;
        private ToolStripStatusLabel statusLabel;

        // Ticket templates
        private List<string> _citations = new List<string>();
        private List<string> _arrests = new List<string>();

        public ExternalMdtForm()
        {
            InitializeComponent();
            FindAndLoadDatabase();

            // Ensure lists are initialized
            if (_citations == null) _citations = new List<string>();
            if (_arrests == null) _arrests = new List<string>();
        }

        private void InitializeComponent()
        {
            this.Text = "PERSISTENT WORLD MOBILE DATA TERMINAL";
            this.Size = new Size(1400, 950);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(10, 10, 20);
            this.ForeColor = Color.White;
            this.Font = new Font("Segoe UI", 10, FontStyle.Regular);
            this.MinimumSize = new Size(1200, 800);

            // ===== MENU STRIP =====
            var menuStrip = new MenuStrip();
            menuStrip.BackColor = Color.FromArgb(30, 30, 40);
            menuStrip.ForeColor = Color.White;
            menuStrip.Renderer = new ToolStripProfessionalRenderer(new MenuStripColorTable());

            var fileMenu = new ToolStripMenuItem("FILE");
            fileMenu.ForeColor = Color.White;

            openToolStripMenuItem = new ToolStripMenuItem("Open Database...");
            openToolStripMenuItem.Click += (s, e) => OpenDatabaseDialog();

            refreshToolStripMenuItem = new ToolStripMenuItem("Refresh Data");
            refreshToolStripMenuItem.Click += (s, e) => RefreshCurrentView();

            exitToolStripMenuItem = new ToolStripMenuItem("Exit");
            exitToolStripMenuItem.Click += (s, e) => this.Close();

            fileMenu.DropDownItems.Add(openToolStripMenuItem);
            fileMenu.DropDownItems.Add(refreshToolStripMenuItem);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(exitToolStripMenuItem);

            menuStrip.Items.Add(fileMenu);

            var helpMenu = new ToolStripMenuItem("HELP");
            helpMenu.ForeColor = Color.White;
            var aboutItem = new ToolStripMenuItem("About");
            aboutItem.Click += (s, e) => MessageBox.Show("Persistent World MDT - External Police Computer\nVersion 1.0", "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
            helpMenu.DropDownItems.Add(aboutItem);
            menuStrip.Items.Add(helpMenu);

            // ===== MAIN PANEL =====
            var mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(15),
                BackColor = Color.FromArgb(10, 10, 20)
            };

            // ===== HEADER =====
            var headerPanel = new Panel
            {
                Height = 90,
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(20, 20, 35),
                Margin = new Padding(0, 0, 0, 10)
            };

            var lblTitle = new Label
            {
                Text = "PERSISTENT WORLD MOBILE DATA TERMINAL",
                Location = new Point(15, 15),
                Size = new Size(900, 35),
                Font = new Font("Courier New", 22, FontStyle.Bold),
                ForeColor = Color.Cyan
            };
            headerPanel.Controls.Add(lblTitle);

            var lblSubtitle = new Label
            {
                Text = "LOS SANTOS POLICE DEPARTMENT - OFFICIAL USE ONLY",
                Location = new Point(15, 55),
                Size = new Size(700, 20),
                Font = new Font("Courier New", 11, FontStyle.Bold),
                ForeColor = Color.Yellow
            };
            headerPanel.Controls.Add(lblSubtitle);

            var lblTime = new Label
            {
                Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Location = new Point(1050, 30),
                Size = new Size(280, 30),
                Font = new Font("Courier New", 12, FontStyle.Bold),
                ForeColor = Color.LightGreen,
                TextAlign = ContentAlignment.MiddleRight
            };
            headerPanel.Controls.Add(lblTime);

            var timer = new Timer { Interval = 1000 };
            timer.Tick += (s, e) => lblTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            timer.Start();

            mainPanel.Controls.Add(headerPanel);

            // ===== SEARCH PANEL =====
            var searchPanel = new Panel
            {
                Height = 130,
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(30, 30, 45),
                Padding = new Padding(15),
                Margin = new Padding(0, 5, 0, 10)
            };

            // Mode selection tabs
            var tabVehicle = new Button
            {
                Text = "VEHICLE SEARCH",
                Location = new Point(15, 15),
                Size = new Size(160, 35),
                BackColor = Color.FromArgb(0, 100, 200),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Courier New", 10, FontStyle.Bold)
            };
            tabVehicle.Click += (s, e) => SwitchMode(SearchMode.Vehicle);

            var tabPerson = new Button
            {
                Text = "PERSON SEARCH",
                Location = new Point(185, 15),
                Size = new Size(160, 35),
                BackColor = Color.FromArgb(64, 64, 64),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Courier New", 10, FontStyle.Bold)
            };
            tabPerson.Click += (s, e) => SwitchMode(SearchMode.Person);

            searchPanel.Controls.Add(tabVehicle);
            searchPanel.Controls.Add(tabPerson);

            // Vehicle search controls
            var vehicleSearchGroup = new GroupBox
            {
                Text = "VEHICLE LOOKUP",
                Location = new Point(15, 60),
                Size = new Size(550, 55),
                ForeColor = Color.White,
                Font = new Font("Courier New", 9, FontStyle.Bold),
                Visible = true
            };

            var lblPlate = new Label
            {
                Text = "LICENSE PLATE:",
                Location = new Point(10, 22),
                Size = new Size(130, 22),
                ForeColor = Color.White,
                Font = new Font("Courier New", 10)
            };
            vehicleSearchGroup.Controls.Add(lblPlate);

            txtVehiclePlate = new TextBox
            {
                Location = new Point(150, 20),
                Size = new Size(220, 25),
                BackColor = Color.FromArgb(50, 50, 60),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                CharacterCasing = CharacterCasing.Upper,
                Font = new Font("Courier New", 10, FontStyle.Bold)
            };
            txtVehiclePlate.KeyDown += TxtSearch_KeyDown;
            txtVehiclePlate.TextChanged += TxtVehiclePlate_TextChanged;
            vehicleSearchGroup.Controls.Add(txtVehiclePlate);

            var btnSearchVehicle = new Button
            {
                Text = "SEARCH",
                Location = new Point(380, 18),
                Size = new Size(120, 28),
                BackColor = Color.FromArgb(0, 120, 0),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Courier New", 9, FontStyle.Bold)
            };
            btnSearchVehicle.Click += (s, e) => PerformVehicleSearch();
            vehicleSearchGroup.Controls.Add(btnSearchVehicle);

            searchPanel.Controls.Add(vehicleSearchGroup);

            // Person search controls
            var personSearchGroup = new GroupBox
            {
                Text = "PERSON LOOKUP",
                Location = new Point(15, 60),
                Size = new Size(700, 55),
                ForeColor = Color.White,
                Font = new Font("Courier New", 9, FontStyle.Bold),
                Visible = false
            };

            var lblFirstName = new Label
            {
                Text = "FIRST:",
                Location = new Point(10, 22),
                Size = new Size(60, 22),
                ForeColor = Color.White,
                Font = new Font("Courier New", 10)
            };
            personSearchGroup.Controls.Add(lblFirstName);

            txtPersonFirstName = new TextBox
            {
                Location = new Point(80, 20),
                Size = new Size(160, 25),
                BackColor = Color.FromArgb(50, 50, 60),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Courier New", 10)
            };
            txtPersonFirstName.KeyDown += TxtSearch_KeyDown;
            txtPersonFirstName.TextChanged += TxtPersonName_TextChanged;
            personSearchGroup.Controls.Add(txtPersonFirstName);

            var lblLastName = new Label
            {
                Text = "LAST:",
                Location = new Point(250, 22),
                Size = new Size(60, 22),
                ForeColor = Color.White,
                Font = new Font("Courier New", 10)
            };
            personSearchGroup.Controls.Add(lblLastName);

            txtPersonLastName = new TextBox
            {
                Location = new Point(320, 20),
                Size = new Size(160, 25),
                BackColor = Color.FromArgb(50, 50, 60),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Courier New", 10)
            };
            txtPersonLastName.KeyDown += TxtSearch_KeyDown;
            txtPersonLastName.TextChanged += TxtPersonName_TextChanged;
            personSearchGroup.Controls.Add(txtPersonLastName);

            var btnSearchPerson = new Button
            {
                Text = "SEARCH",
                Location = new Point(500, 18),
                Size = new Size(120, 28),
                BackColor = Color.FromArgb(0, 120, 0),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Courier New", 9, FontStyle.Bold)
            };
            btnSearchPerson.Click += (s, e) => PerformPersonSearch();
            personSearchGroup.Controls.Add(btnSearchPerson);

            searchPanel.Controls.Add(personSearchGroup);

            // Suggestions ListBox
            lstSuggestions = new ListBox
            {
                Location = new Point(580, 45),
                Size = new Size(320, 150),
                BackColor = Color.FromArgb(40, 40, 50),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Courier New", 10),
                Visible = false
            };
            lstSuggestions.SelectedIndexChanged += LstSuggestions_SelectedIndexChanged;
            lstSuggestions.MouseDoubleClick += (s, e) => SelectSuggestion();
            lstSuggestions.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && lstSuggestions.SelectedItem != null)
                {
                    SelectSuggestion();
                    e.SuppressKeyPress = true;
                }
            };
            searchPanel.Controls.Add(lstSuggestions);

            mainPanel.Controls.Add(searchPanel);

            // ===== CONTENT PANEL (Split into two columns) =====
            var contentPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(0, 10, 0, 0),
                BackColor = Color.FromArgb(10, 10, 20)
            };
            // LEFT COLUMN (35%) - Search Results and Citation History
            contentPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35F));
            // RIGHT COLUMN (65%) - Details and Actions
            contentPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65F));

            // ROW HEIGHTS - Give more room to details (top row 60%, bottom row 40%)
            contentPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 60F));
            contentPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));

            // ===== LEFT TOP - SEARCH RESULTS =====
            var resultsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 30),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(10)
            };

            var lblResults = new Label
            {
                Text = "► SEARCH RESULTS",
                Location = new Point(10, 10),
                Size = new Size(400, 25),
                Font = new Font("Courier New", 12, FontStyle.Bold),
                ForeColor = Color.Yellow
            };
            resultsPanel.Controls.Add(lblResults);

            lstResults = new ListBox
            {
                Location = new Point(10, 40),
                Size = new Size(440, 380),
                BackColor = Color.FromArgb(30, 30, 40),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Courier New", 10),
                IntegralHeight = false
            };
            lstResults.SelectedIndexChanged += LstResults_SelectedIndexChanged;
            lstResults.MouseDoubleClick += (s, e) => {
                if (lstResults.SelectedItem != null && _personResults != null)
                {
                    int index = lstResults.SelectedIndex;
                    if (index >= 0 && index < _personResults.Count)
                    {
                        _currentPerson = _personResults[index];
                        DisplayPersonDetails();
                        LoadPersonTickets(Convert.ToInt32(_currentPerson["id"]));
                    }
                }
            };
            resultsPanel.Controls.Add(lstResults);

            contentPanel.Controls.Add(resultsPanel, 0, 0);

            // ===== RIGHT TOP - DETAILS (GIVEN MORE ROOM) =====
            var detailsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 30),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(10)
            };

            var lblDetails = new Label
            {
                Text = "► DETAILS",
                Location = new Point(10, 10),
                Size = new Size(400, 25),
                Font = new Font("Courier New", 12, FontStyle.Bold),
                ForeColor = Color.Yellow
            };
            detailsPanel.Controls.Add(lblDetails);

            txtDetails = new RichTextBox
            {
                Location = new Point(10, 40),
                Size = new Size(850, 420),
                BackColor = Color.FromArgb(10, 10, 20),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                ReadOnly = true,
                Font = new Font("Courier New", 10),
                WordWrap = true
            };
            detailsPanel.Controls.Add(txtDetails);

            contentPanel.Controls.Add(detailsPanel, 1, 0);

            // ===== LEFT BOTTOM - CITATION HISTORY =====
            var historyPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 30),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(10)
            };

            var lblHistory = new Label
            {
                Text = "► CITATION HISTORY",
                Location = new Point(10, 10),
                Size = new Size(400, 25),
                Font = new Font("Courier New", 12, FontStyle.Bold),
                ForeColor = Color.Yellow
            };
            historyPanel.Controls.Add(lblHistory);

            gridTickets = new DataGridView
            {
                Location = new Point(10, 40),
                Size = new Size(440, 220),
                BackgroundColor = Color.FromArgb(30, 30, 40),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                Font = new Font("Courier New", 9)
            };
            gridTickets.EnableHeadersVisualStyles = false;
            gridTickets.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(0, 70, 140);
            gridTickets.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            gridTickets.ColumnHeadersDefaultCellStyle.Font = new Font("Courier New", 9, FontStyle.Bold);
            gridTickets.RowsDefaultCellStyle.BackColor = Color.FromArgb(40, 40, 50);
            gridTickets.RowsDefaultCellStyle.ForeColor = Color.White;
            gridTickets.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(50, 50, 60);
            gridTickets.CellMouseDoubleClick += (s, e) => {
                if (e.RowIndex >= 0 && gridTickets.SelectedRows.Count > 0)
                {
                    var offense = gridTickets.SelectedRows[0].Cells["Offense"].Value?.ToString() ?? "Unknown";
                    var fine = gridTickets.SelectedRows[0].Cells["Fine"].Value?.ToString() ?? "0";
                    var date = gridTickets.SelectedRows[0].Cells["Date"].Value?.ToString() ?? "Unknown";
                    MessageBox.Show($"Offense: {offense}\nFine: ${fine}\nDate: {date}", "Ticket Details",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            historyPanel.Controls.Add(gridTickets);

            contentPanel.Controls.Add(historyPanel, 0, 1);

            // ===== RIGHT BOTTOM - ACTIONS (MADE SMALLER) =====
            var actionsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 30),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(10)
            };

            var lblActions = new Label
            {
                Text = "► ACTIONS",
                Location = new Point(10, 10),
                Size = new Size(400, 25),
                Font = new Font("Courier New", 12, FontStyle.Bold),
                ForeColor = Color.Yellow
            };
            actionsPanel.Controls.Add(lblActions);

            // Selected person info - Made more compact
            var selectedGroup = new GroupBox
            {
                Text = "SELECTED PERSON",
                Location = new Point(10, 40),
                Size = new Size(850, 70),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(30, 30, 40),
                Font = new Font("Courier New", 10, FontStyle.Bold)
            };

            lblPersonName = new Label
            {
                Text = "None selected",
                Location = new Point(15, 20),
                Size = new Size(400, 25),
                ForeColor = Color.Cyan,
                Font = new Font("Courier New", 12, FontStyle.Bold)
            };
            selectedGroup.Controls.Add(lblPersonName);

            lblWantedStatus = new Label
            {
                Text = "",
                Location = new Point(15, 45),
                Size = new Size(150, 20),
                ForeColor = Color.White,
                Font = new Font("Courier New", 9)
            };
            selectedGroup.Controls.Add(lblWantedStatus);

            lblIncarceratedStatus = new Label
            {
                Text = "",
                Location = new Point(180, 45),
                Size = new Size(150, 20),
                ForeColor = Color.White,
                Font = new Font("Courier New", 9)
            };
            selectedGroup.Controls.Add(lblIncarceratedStatus);

            actionsPanel.Controls.Add(selectedGroup);

            // Issue Ticket Button - Made smaller
            btnIssueTicket = new Button
            {
                Text = "ISSUE TICKET",
                Location = new Point(10, 120),
                Size = new Size(180, 40),
                BackColor = Color.FromArgb(0, 100, 200),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Courier New", 12, FontStyle.Bold)
            };
            btnIssueTicket.Click += BtnIssueTicket_Click;
            btnIssueTicket.MouseEnter += (s, e) => btnIssueTicket.BackColor = Color.FromArgb(0, 120, 255);
            btnIssueTicket.MouseLeave += (s, e) => btnIssueTicket.BackColor = Color.FromArgb(0, 100, 200);
            actionsPanel.Controls.Add(btnIssueTicket);

            // Quick stats - Made smaller and moved to the right
            var statsGroup = new GroupBox
            {
                Text = "QUICK STATS",
                Location = new Point(200, 120),
                Size = new Size(660, 70),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(30, 30, 40),
                Font = new Font("Courier New", 9, FontStyle.Bold)
            };

            var lblWantedCount = new Label
            {
                Text = "Wanted Persons: 0",
                Location = new Point(10, 20),
                Size = new Size(150, 20),
                ForeColor = Color.LightCoral,
                Font = new Font("Courier New", 9)
            };
            statsGroup.Controls.Add(lblWantedCount);

            var lblIncarceratedCount = new Label
            {
                Text = "Incarcerated: 0",
                Location = new Point(10, 45),
                Size = new Size(150, 20),
                ForeColor = Color.LightGray,
                Font = new Font("Courier New", 9)
            };
            statsGroup.Controls.Add(lblIncarceratedCount);

            var lblVehiclesStolen = new Label
            {
                Text = "Stolen Vehicles: 0",
                Location = new Point(200, 20),
                Size = new Size(150, 20),
                ForeColor = Color.Orange,
                Font = new Font("Courier New", 9)
            };
            statsGroup.Controls.Add(lblVehiclesStolen);

            var lblVehiclesImpounded = new Label
            {
                Text = "Impounded: 0",
                Location = new Point(200, 45),
                Size = new Size(150, 20),
                ForeColor = Color.Yellow,
                Font = new Font("Courier New", 9)
            };
            statsGroup.Controls.Add(lblVehiclesImpounded);

            var lblWarrants = new Label
            {
                Text = "Active Warrants: 0",
                Location = new Point(400, 20),
                Size = new Size(150, 20),
                ForeColor = Color.LightBlue,
                Font = new Font("Courier New", 9)
            };
            statsGroup.Controls.Add(lblWarrants);

            var lblRegistered = new Label
            {
                Text = "Registered: 0",
                Location = new Point(400, 45),
                Size = new Size(150, 20),
                ForeColor = Color.LightGreen,
                Font = new Font("Courier New", 9)
            };
            statsGroup.Controls.Add(lblRegistered);

            actionsPanel.Controls.Add(statsGroup);

            contentPanel.Controls.Add(actionsPanel, 1, 1);

            mainPanel.Controls.Add(contentPanel);

            // ===== STATUS STRIP =====
            statusStrip = new StatusStrip
            {
                BackColor = Color.FromArgb(30, 30, 40),
                ForeColor = Color.White
            };
            statusLabel = new ToolStripStatusLabel("READY - No database connected");
            statusLabel.ForeColor = Color.Yellow;
            statusLabel.Font = new Font("Courier New", 9);
            statusStrip.Items.Add(statusLabel);

            // Add all to form
            this.Controls.Add(mainPanel);
            this.Controls.Add(menuStrip);
            this.Controls.Add(statusStrip);
            this.MainMenuStrip = menuStrip;

            // Timer for suggestions
            suggestionTimer = new Timer { Interval = 300 };
            suggestionTimer.Tick += SuggestionTimer_Tick;
        }

        // Custom renderer for menu strip
        public class MenuStripColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected
            {
                get { return Color.FromArgb(50, 50, 70); }
            }
            public override Color MenuItemSelectedGradientBegin
            {
                get { return Color.FromArgb(40, 40, 60); }
            }
            public override Color MenuItemSelectedGradientEnd
            {
                get { return Color.FromArgb(50, 50, 70); }
            }
        }

        // Ticket issuance dialog
        private class IssueTicketDialog : Form
        {
            public int SelectedVehicleId { get; private set; } = 1;
            public string SelectedOffense { get; private set; }
            public string TicketLocation { get; private set; }
            public int Fine { get; private set; }
            public int JailDays { get; private set; }
            public bool IsArrest { get; private set; }

            private ComboBox cmbVehicle;
            private ComboBox cmbOffense;
            private TextBox txtLocation;
            private NumericUpDown numFine;
            private NumericUpDown numJailDays;
            private RadioButton rbCitation;
            private RadioButton rbArrest;
            private List<Dictionary<string, object>> _availableVehicles;
            private List<string> _citations;
            private List<string> _arrests;

            public IssueTicketDialog(string personName, List<Dictionary<string, object>> availableVehicles, List<string> citations, List<string> arrests)
            {
                // FIX: Add null checks and default values
                _availableVehicles = availableVehicles ?? new List<Dictionary<string, object>>();
                _citations = citations ?? new List<string>();
                _arrests = arrests ?? new List<string>();

                // Add default values if lists are empty
                if (_citations.Count == 0)
                {
                    _citations.AddRange(new[] {
                        "SPEEDING (1-10 MPH) - Speeding 1-10 mph over limit",
                        "SPEEDING (11-15 MPH) - Speeding 11-15 mph over limit",
                        "SPEEDING (16-25 MPH) - Speeding 16-25 mph over limit",
                        "SPEEDING (25+ MPH) - Speeding 25+ mph over limit",
                        "RUNNING RED LIGHT - Failed to stop at red light",
                        "NO INSURANCE - Driving without insurance",
                        "EXPIRED LICENSE - Driver's license expired"
                    });
                }

                if (_arrests.Count == 0)
                {
                    _arrests.AddRange(new[] {
                        "DUI - Driving under influence",
                        "HIT AND RUN - Leaving scene of accident",
                        "GRAND THEFT AUTO - Stolen vehicle"
                    });
                }

                this.Text = "Issue Ticket";
                this.Size = new Size(500, 420);
                this.StartPosition = FormStartPosition.CenterParent;
                this.FormBorderStyle = FormBorderStyle.FixedDialog;
                this.MaximizeBox = false;
                this.MinimizeBox = false;
                this.BackColor = Color.FromArgb(30, 30, 40);
                this.ForeColor = Color.White;

                int yPos = 20;
                int labelWidth = 100;

                // Person name
                var lblPerson = new Label
                {
                    Text = $"Issuing to: {personName}",
                    Location = new Point(20, yPos),
                    Size = new Size(400, 25),
                    ForeColor = Color.Cyan,
                    Font = new Font("Courier New", 10, FontStyle.Bold)
                };
                Controls.Add(lblPerson);
                yPos += 35;

                // Vehicle selection
                var lblVehicle = new Label
                {
                    Text = "Vehicle:",
                    Location = new Point(20, yPos),
                    Size = new Size(labelWidth, 25),
                    ForeColor = Color.White
                };
                Controls.Add(lblVehicle);

                cmbVehicle = new ComboBox
                {
                    Location = new Point(130, yPos),
                    Size = new Size(300, 25),
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Color.FromArgb(50, 50, 60),
                    ForeColor = Color.White
                };
                cmbVehicle.Items.Add("No Vehicle");
                foreach (var v in _availableVehicles)
                {
                    string plate = v["license_plate"].ToString();
                    string model = v["vehicle_model"].ToString();
                    cmbVehicle.Items.Add($"{plate} - {model}");
                }
                cmbVehicle.SelectedIndex = 0;
                Controls.Add(cmbVehicle);
                yPos += 35;

                // Ticket type
                var lblType = new Label
                {
                    Text = "Type:",
                    Location = new Point(20, yPos),
                    Size = new Size(labelWidth, 25),
                    ForeColor = Color.White
                };
                Controls.Add(lblType);

                rbCitation = new RadioButton
                {
                    Text = "Citation",
                    Location = new Point(130, yPos),
                    Size = new Size(100, 25),
                    ForeColor = Color.White,
                    Checked = true
                };
                Controls.Add(rbCitation);

                rbArrest = new RadioButton
                {
                    Text = "Arrest",
                    Location = new Point(240, yPos),
                    Size = new Size(100, 25),
                    ForeColor = Color.White
                };
                Controls.Add(rbArrest);
                yPos += 35;

                // Offense
                var lblOffense = new Label
                {
                    Text = "Offense:",
                    Location = new Point(20, yPos),
                    Size = new Size(labelWidth, 25),
                    ForeColor = Color.White
                };
                Controls.Add(lblOffense);

                cmbOffense = new ComboBox
                {
                    Location = new Point(130, yPos),
                    Size = new Size(300, 25),
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Color.FromArgb(50, 50, 60),
                    ForeColor = Color.White
                };
                Controls.Add(cmbOffense);
                yPos += 35;

                // Location
                var lblLocationLabel = new Label
                {
                    Text = "Location:",
                    Location = new Point(20, yPos),
                    Size = new Size(labelWidth, 25),
                    ForeColor = Color.White
                };
                Controls.Add(lblLocationLabel);

                txtLocation = new TextBox
                {
                    Location = new Point(130, yPos),
                    Size = new Size(300, 25),
                    BackColor = Color.FromArgb(50, 50, 60),
                    ForeColor = Color.White,
                    Text = "Los Santos"
                };
                Controls.Add(txtLocation);
                yPos += 35;

                // Fine
                var lblFine = new Label
                {
                    Text = "Fine: $",
                    Location = new Point(20, yPos),
                    Size = new Size(labelWidth, 25),
                    ForeColor = Color.White
                };
                Controls.Add(lblFine);

                numFine = new NumericUpDown
                {
                    Location = new Point(130, yPos),
                    Size = new Size(120, 25),
                    Minimum = 0,
                    Maximum = 10000,
                    BackColor = Color.FromArgb(50, 50, 60),
                    ForeColor = Color.White
                };
                Controls.Add(numFine);
                yPos += 35;

                // Jail Days
                var lblJail = new Label
                {
                    Text = "Jail Days:",
                    Location = new Point(20, yPos),
                    Size = new Size(labelWidth, 25),
                    ForeColor = Color.White
                };
                Controls.Add(lblJail);

                numJailDays = new NumericUpDown
                {
                    Location = new Point(130, yPos),
                    Size = new Size(80, 25),
                    Minimum = 0,
                    Maximum = 999,
                    BackColor = Color.FromArgb(50, 50, 60),
                    ForeColor = Color.White,
                    Visible = false
                };
                Controls.Add(numJailDays);
                yPos += 45;

                // Buttons
                var btnIssue = new Button
                {
                    Text = "ISSUE",
                    Location = new Point(130, yPos),
                    Size = new Size(100, 35),
                    BackColor = Color.FromArgb(0, 100, 200),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Courier New", 10, FontStyle.Bold)
                };
                btnIssue.Click += (s, e) => ValidateAndClose();
                Controls.Add(btnIssue);

                var btnCancel = new Button
                {
                    Text = "CANCEL",
                    Location = new Point(240, yPos),
                    Size = new Size(100, 35),
                    BackColor = Color.FromArgb(100, 0, 0),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat
                };
                btnCancel.Click += (s, e) => DialogResult = DialogResult.Cancel;
                Controls.Add(btnCancel);

                // FIX: Set up event handlers AFTER all controls are created
                rbCitation.CheckedChanged += (s, e) => UpdateOffenseList();
                rbArrest.CheckedChanged += (s, e) => UpdateOffenseList();

                // FIX: Initialize the offense list after everything is set up
                UpdateOffenseList();
            }

            private void UpdateOffenseList()
            {
                // FIX: Add null check for cmbOffense
                if (cmbOffense == null) return;

                cmbOffense.Items.Clear();
                IsArrest = rbArrest.Checked;

                if (IsArrest)
                {
                    foreach (var a in _arrests)
                        cmbOffense.Items.Add(a);
                    if (numJailDays != null)
                    {
                        numJailDays.Visible = true;
                        numFine.Enabled = false;
                        numFine.Value = 0;
                    }
                }
                else
                {
                    foreach (var c in _citations)
                        cmbOffense.Items.Add(c);
                    if (numJailDays != null)
                    {
                        numJailDays.Visible = false;
                        numFine.Enabled = true;
                    }
                }

                if (cmbOffense.Items.Count > 0)
                    cmbOffense.SelectedIndex = 0;
            }

            private void ValidateAndClose()
            {
                if (cmbOffense.SelectedItem == null)
                {
                    MessageBox.Show("Please select an offense", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                SelectedOffense = cmbOffense.SelectedItem.ToString();
                TicketLocation = txtLocation.Text;
                Fine = (int)numFine.Value;
                JailDays = (int)numJailDays.Value;

                if (cmbVehicle.SelectedIndex > 0 && _availableVehicles.Count >= cmbVehicle.SelectedIndex)
                {
                    SelectedVehicleId = Convert.ToInt32(_availableVehicles[cmbVehicle.SelectedIndex - 1]["id"]);
                }

                DialogResult = DialogResult.OK;
                Close();
            }
        }

        private void FindAndLoadDatabase()
        {
            // Try multiple possible locations
            string[] possiblePaths = {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Plugins", "LSPDFR", "PersistentWorld", "PersistentWorld.db"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Plugins", "LSPDFR", "PersistentWorld", "PersistentWorld.db"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "LSPDFR", "PersistentWorld", "PersistentWorld.db"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PersistentWorld", "PersistentWorld.db"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PersistentWorld.db"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "PersistentWorld.db")
            };

            UpdateStatus("SEARCHING FOR DATABASE...", Color.Yellow);

            foreach (string path in possiblePaths)
            {
                try
                {
                    string fullPath = Path.GetFullPath(path);
                    if (File.Exists(fullPath))
                    {
                        _dbPath = fullPath;
                        break;
                    }
                }
                catch { }
            }

            if (_dbPath != null)
            {
                ConnectToDatabase(_dbPath);
            }
            else
            {
                UpdateStatus("DATABASE NOT FOUND - Use File > Open Database", Color.Red);
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

                this.Text = $"PERSISTENT WORLD MDT - {Path.GetFileName(path)}";
                UpdateStatus($"CONNECTED TO: {path}", Color.LightGreen);

                LoadTicketTemplates();
                LoadStats();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error connecting to database: {ex.Message}", "ERROR",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatus("CONNECTION FAILED", Color.Red);
            }
        }

        private void LoadStats()
        {
            if (_connection == null) return;

            try
            {
                // Find the stats labels
                foreach (Control c in this.Controls)
                {
                    if (c is Panel mainPanel)
                    {
                        foreach (Control panel in mainPanel.Controls)
                        {
                            if (panel is TableLayoutPanel contentPanel)
                            {
                                foreach (Control content in contentPanel.Controls)
                                {
                                    if (content is Panel actionsPanel)
                                    {
                                        foreach (Control action in actionsPanel.Controls)
                                        {
                                            if (action is GroupBox statsGroup && statsGroup.Text == "QUICK STATS")
                                            {
                                                foreach (Control stat in statsGroup.Controls)
                                                {
                                                    if (stat is Label label)
                                                    {
                                                        if (label.Text.StartsWith("Wanted"))
                                                        {
                                                            using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM peds WHERE is_wanted = 1", _connection))
                                                            {
                                                                int wanted = Convert.ToInt32(cmd.ExecuteScalar());
                                                                label.Text = $"Wanted Persons: {wanted}";
                                                            }
                                                        }
                                                        else if (label.Text.StartsWith("Incarcerated"))
                                                        {
                                                            using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM peds WHERE is_incarcerated = 1", _connection))
                                                            {
                                                                int incarcerated = Convert.ToInt32(cmd.ExecuteScalar());
                                                                label.Text = $"Incarcerated: {incarcerated}";
                                                            }
                                                        }
                                                        else if (label.Text.StartsWith("Stolen"))
                                                        {
                                                            using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM vehicles WHERE is_stolen = 1", _connection))
                                                            {
                                                                int stolen = Convert.ToInt32(cmd.ExecuteScalar());
                                                                label.Text = $"Stolen Vehicles: {stolen}";
                                                            }
                                                        }
                                                        else if (label.Text.StartsWith("Impounded"))
                                                        {
                                                            using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM vehicles WHERE is_impounded = 1", _connection))
                                                            {
                                                                int impounded = Convert.ToInt32(cmd.ExecuteScalar());
                                                                label.Text = $"Impounded: {impounded}";
                                                            }
                                                        }
                                                        else if (label.Text.StartsWith("Active Warrants"))
                                                        {
                                                            using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM peds WHERE is_wanted = 1", _connection))
                                                            {
                                                                int warrants = Convert.ToInt32(cmd.ExecuteScalar());
                                                                label.Text = $"Active Warrants: {warrants}";
                                                            }
                                                        }
                                                        else if (label.Text.StartsWith("Registered"))
                                                        {
                                                            using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM peds WHERE is_active = 1", _connection))
                                                            {
                                                                int registered = Convert.ToInt32(cmd.ExecuteScalar());
                                                                label.Text = $"Registered: {registered}";
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void UpdateStatus(string text, Color color)
        {
            if (statusLabel != null)
            {
                statusLabel.Text = text;
                statusLabel.ForeColor = color;
            }
        }

        private void RefreshCurrentView()
        {
            if (_currentPerson != null)
            {
                LoadPersonTickets(Convert.ToInt32(_currentPerson["id"]));
                DisplayPersonDetails();
            }
            else if (_currentVehicle != null)
            {
                DisplayVehicleDetails();
                LoadVehicleTickets(Convert.ToInt32(_currentVehicle["id"]));
            }
            LoadStats();
        }

        private void SwitchMode(SearchMode mode)
        {
            _currentSearchMode = mode;

            // Find search panel controls
            foreach (Control c in this.Controls)
            {
                if (c is Panel mainPanel)
                {
                    foreach (Control panel in mainPanel.Controls)
                    {
                        if (panel is Panel searchPanel)
                        {
                            foreach (Control btn in searchPanel.Controls)
                            {
                                if (btn is Button tabBtn && (tabBtn.Text == "VEHICLE SEARCH" || tabBtn.Text == "PERSON SEARCH"))
                                {
                                    tabBtn.BackColor = Color.FromArgb(64, 64, 64);
                                }
                                if (btn is GroupBox group)
                                {
                                    if (group.Text == "VEHICLE LOOKUP")
                                        group.Visible = (mode == SearchMode.Vehicle);
                                    if (group.Text == "PERSON LOOKUP")
                                        group.Visible = (mode == SearchMode.Person);
                                }
                            }

                            // Find and highlight active tab
                            foreach (Control btn in searchPanel.Controls)
                            {
                                if (btn is Button tabBtn)
                                {
                                    if (mode == SearchMode.Vehicle && tabBtn.Text == "VEHICLE SEARCH")
                                        tabBtn.BackColor = Color.FromArgb(0, 100, 200);
                                    if (mode == SearchMode.Person && tabBtn.Text == "PERSON SEARCH")
                                        tabBtn.BackColor = Color.FromArgb(0, 100, 200);
                                }
                            }
                        }
                    }
                }
            }

            ClearResults();
        }

        private void ClearResults()
        {
            lstResults.Items.Clear();
            txtDetails.Clear();
            gridTickets.DataSource = null;
            _currentVehicle = null;
            _currentPerson = null;
            _personResults = null;
            lblPersonName.Text = "None selected";
            lblWantedStatus.Text = "";
            lblIncarceratedStatus.Text = "";
        }

        private void TxtVehiclePlate_TextChanged(object sender, EventArgs e)
        {
            suggestionTimer.Stop();
            suggestionTimer.Start();
        }

        private void TxtPersonName_TextChanged(object sender, EventArgs e)
        {
            suggestionTimer.Stop();
            suggestionTimer.Start();
        }

        private void SuggestionTimer_Tick(object sender, EventArgs e)
        {
            suggestionTimer.Stop();
            ShowSuggestions();
        }

        private void ShowSuggestions()
        {
            if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            {
                lstSuggestions.Visible = false;
                return;
            }

            lstSuggestions.Items.Clear();

            if (_currentSearchMode == SearchMode.Vehicle)
            {
                if (txtVehiclePlate.Text.Length < 1) return;

                string search = txtVehiclePlate.Text.ToUpper();
                var suggestions = new List<string>();

                using (var cmd = new SQLiteCommand(@"
                    SELECT v.license_plate, v.vehicle_model, 
                           p.first_name || ' ' || p.last_name as owner_name
                    FROM vehicles v
                    LEFT JOIN peds p ON v.owner_type = 'person' AND v.owner_id = p.id
                    WHERE v.license_plate LIKE @search
                    ORDER BY v.license_plate LIMIT 10", _connection))
                {
                    cmd.Parameters.AddWithValue("@search", search + "%");
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            suggestions.Add($"{reader["license_plate"]} - {reader["vehicle_model"]} ({reader["owner_name"]})");
                        }
                    }
                }

                if (suggestions.Count > 0)
                {
                    lstSuggestions.Items.AddRange(suggestions.ToArray());
                    lstSuggestions.Visible = true;
                    lstSuggestions.BringToFront();
                }
                else
                {
                    lstSuggestions.Visible = false;
                }
            }
            else
            {
                string firstName = txtPersonFirstName.Text;
                string lastName = txtPersonLastName.Text;

                if (firstName.Length < 1 && lastName.Length < 1) return;

                var suggestions = new List<string>();

                using (var cmd = new SQLiteCommand(@"
                    SELECT first_name, last_name, license_number
                    FROM peds
                    WHERE (@firstName = '' OR first_name LIKE @firstNamePattern)
                      AND (@lastName = '' OR last_name LIKE @lastNamePattern)
                    ORDER BY last_name, first_name LIMIT 10", _connection))
                {
                    cmd.Parameters.AddWithValue("@firstName", firstName);
                    cmd.Parameters.AddWithValue("@firstNamePattern", firstName + "%");
                    cmd.Parameters.AddWithValue("@lastName", lastName);
                    cmd.Parameters.AddWithValue("@lastNamePattern", lastName + "%");

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            suggestions.Add($"{reader["first_name"]} {reader["last_name"]} - Lic: {reader["license_number"]}");
                        }
                    }
                }

                if (suggestions.Count > 0)
                {
                    lstSuggestions.Items.AddRange(suggestions.ToArray());
                    lstSuggestions.Visible = true;
                    lstSuggestions.BringToFront();
                }
                else
                {
                    lstSuggestions.Visible = false;
                }
            }
        }

        private void LstSuggestions_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Handle mouse click selection
        }

        private void SelectSuggestion()
        {
            if (lstSuggestions.SelectedItem != null)
            {
                string selected = lstSuggestions.SelectedItem.ToString();

                if (_currentSearchMode == SearchMode.Vehicle)
                {
                    string plate = selected.Split('-')[0].Trim();
                    txtVehiclePlate.Text = plate;
                    PerformVehicleSearch();
                }
                else
                {
                    string name = selected.Split('-')[0].Trim();
                    string[] nameParts = name.Split(' ');
                    if (nameParts.Length >= 2)
                    {
                        txtPersonFirstName.Text = nameParts[0];
                        txtPersonLastName.Text = string.Join(" ", nameParts, 1, nameParts.Length - 1);
                        PerformPersonSearch();
                    }
                }

                lstSuggestions.Visible = false;
            }
        }

        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (lstSuggestions.Visible && lstSuggestions.SelectedItem != null)
                {
                    SelectSuggestion();
                }
                else if (_currentSearchMode == SearchMode.Vehicle)
                {
                    PerformVehicleSearch();
                }
                else
                {
                    PerformPersonSearch();
                }
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Down && lstSuggestions.Visible)
            {
                if (lstSuggestions.Items.Count > 0)
                {
                    lstSuggestions.SelectedIndex = 0;
                    lstSuggestions.Focus();
                }
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                lstSuggestions.Visible = false;
            }
        }

        private void LstResults_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lstResults.SelectedItem != null && _personResults != null)
            {
                int index = lstResults.SelectedIndex;
                if (index >= 0 && index < _personResults.Count)
                {
                    _currentPerson = _personResults[index];
                    DisplayPersonDetails();
                    LoadPersonTickets(Convert.ToInt32(_currentPerson["id"]));

                    // Update the UI to show person is selected
                    lblPersonName.Text = $"{_currentPerson["first_name"]} {_currentPerson["last_name"]}";

                    // Update status labels
                    bool isWanted = _currentPerson.ContainsKey("is_wanted") && Convert.ToBoolean(_currentPerson["is_wanted"]);
                    bool isIncarcerated = _currentPerson.ContainsKey("is_incarcerated") && Convert.ToBoolean(_currentPerson["is_incarcerated"]);

                    lblWantedStatus.Text = isWanted ? "⚠ WANTED" : "";
                    lblWantedStatus.ForeColor = isWanted ? Color.Red : Color.White;

                    lblIncarceratedStatus.Text = isIncarcerated ? "⛓ INCARCERATED" : "";
                    lblIncarceratedStatus.ForeColor = isIncarcerated ? Color.Red : Color.White;
                }
            }
        }

        private void LoadTicketTemplates()
        {
            // FIX: Ensure lists are initialized
            if (_citations == null) _citations = new List<string>();
            if (_arrests == null) _arrests = new List<string>();

            _citations.Clear();
            _arrests.Clear();

            if (_connection == null)
            {
                // Add defaults even without connection
                _citations.AddRange(new[] {
                    "SPEEDING (1-10 MPH) - Speeding 1-10 mph over limit",
                    "SPEEDING (11-15 MPH) - Speeding 11-15 mph over limit",
                    "SPEEDING (16-25 MPH) - Speeding 16-25 mph over limit",
                    "SPEEDING (25+ MPH) - Speeding 25+ mph over limit",
                    "RUNNING RED LIGHT - Failed to stop at red light",
                    "NO INSURANCE - Driving without insurance",
                    "EXPIRED LICENSE - Driver's license expired"
                });

                _arrests.AddRange(new[] {
                    "DUI - Driving under influence",
                    "HIT AND RUN - Leaving scene of accident",
                    "GRAND THEFT AUTO - Stolen vehicle"
                });
                return;
            }

            // Load from citations.ini or use defaults
            string gtaPath = AppDomain.CurrentDomain.BaseDirectory;
            string configPath = Path.Combine(gtaPath, "..", "..", "..", "..", "Plugins", "LSPDFR", "PersistentWorld", "citations.ini");

            if (File.Exists(configPath))
            {
                string[] lines = File.ReadAllLines(configPath);
                string currentSection = "";

                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith(";") || trimmed.StartsWith("#") || string.IsNullOrEmpty(trimmed))
                        continue;

                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentSection = trimmed.Substring(1, trimmed.Length - 2).ToUpper();
                        continue;
                    }

                    string[] parts = trimmed.Split('|');
                    if (parts.Length >= 2)
                    {
                        string title = parts[0].Trim();
                        string desc = parts[1].Trim();
                        string display = $"{title} - {desc}";

                        if (currentSection == "FELONIES_ARRESTABLE")
                            _arrests.Add(display);
                        else
                            _citations.Add(display);
                    }
                }
            }

            // Add defaults if none loaded
            if (_citations.Count == 0)
            {
                _citations.AddRange(new[] {
                    "SPEEDING (1-10 MPH) - Speeding 1-10 mph over limit",
                    "SPEEDING (11-15 MPH) - Speeding 11-15 mph over limit",
                    "RUNNING RED LIGHT - Failed to stop at red light",
                    "NO INSURANCE - Driving without insurance",
                    "EXPIRED LICENSE - Driver's license expired"
                });
            }

            if (_arrests.Count == 0)
            {
                _arrests.AddRange(new[] {
                    "DUI - Driving under influence",
                    "HIT AND RUN - Leaving scene of accident",
                    "GRAND THEFT AUTO - Stolen vehicle"
                });
            }
        }

        private void PerformVehicleSearch()
        {
            if (_connection == null)
            {
                MessageBox.Show("Please open a database first (File > Open Database)", "NO DATABASE",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(txtVehiclePlate.Text))
            {
                MessageBox.Show("Please enter a license plate", "ERROR",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string plate = txtVehiclePlate.Text.ToUpper().Trim();

            using (var cmd = new SQLiteCommand(@"
                SELECT 
                    v.*,
                    CASE 
                        WHEN v.owner_type = 'person' THEN p.first_name || ' ' || p.last_name
                        WHEN v.owner_type = 'company' THEN c.name
                        ELSE 'Unknown'
                    END as owner_name,
                    p.id as ped_id,
                    p.first_name,
                    p.last_name,
                    p.license_status,
                    p.is_wanted,
                    p.is_incarcerated
                FROM vehicles v
                LEFT JOIN peds p ON v.owner_type = 'person' AND v.owner_id = p.id
                LEFT JOIN companies c ON v.owner_type = 'company' AND v.owner_id = c.id
                WHERE v.license_plate = @plate", _connection))
            {
                cmd.Parameters.AddWithValue("@plate", plate);

                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        _currentVehicle = new Dictionary<string, object>();
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            _currentVehicle[reader.GetName(i)] = reader.GetValue(i);
                        }

                        DisplayVehicleDetails();
                        LoadVehicleTickets(Convert.ToInt32(_currentVehicle["id"]));

                        // If owner is a person, also load their info
                        if (_currentVehicle.ContainsKey("ped_id") && _currentVehicle["ped_id"] != DBNull.Value)
                        {
                            int pedId = Convert.ToInt32(_currentVehicle["ped_id"]);
                            LoadPersonById(pedId);
                        }

                        lstResults.Items.Clear();
                        lstResults.Items.Add($"{_currentVehicle["license_plate"]} - {_currentVehicle["vehicle_model"]} ({_currentVehicle["owner_name"]})");
                        lstResults.SelectedIndex = 0;
                    }
                    else
                    {
                        MessageBox.Show($"No vehicle found with plate: {plate}", "NOT FOUND",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        ClearResults();
                    }
                }
            }
        }

        private void PerformPersonSearch()
        {
            if (_connection == null)
            {
                MessageBox.Show("Please open a database first (File > Open Database)", "NO DATABASE",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string firstName = txtPersonFirstName.Text.Trim();
            string lastName = txtPersonLastName.Text.Trim();

            if (string.IsNullOrEmpty(firstName) && string.IsNullOrEmpty(lastName))
            {
                MessageBox.Show("Please enter a first or last name", "ERROR",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var persons = new List<Dictionary<string, object>>();

            using (var cmd = new SQLiteCommand(@"
                SELECT 
                    p.*,
                    e.company_id,
                    c.name as employer_name,
                    e.job_title
                FROM peds p
                LEFT JOIN employment e ON p.id = e.ped_id AND e.is_current = 1
                LEFT JOIN companies c ON e.company_id = c.id
                WHERE (@firstName = '' OR p.first_name LIKE @firstNamePattern)
                  AND (@lastName = '' OR p.last_name LIKE @lastNamePattern)
                ORDER BY p.last_name, p.first_name", _connection))
            {
                cmd.Parameters.AddWithValue("@firstName", firstName);
                cmd.Parameters.AddWithValue("@firstNamePattern", firstName + "%");
                cmd.Parameters.AddWithValue("@lastName", lastName);
                cmd.Parameters.AddWithValue("@lastNamePattern", lastName + "%");

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var person = new Dictionary<string, object>();
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            person[reader.GetName(i)] = reader.GetValue(i);
                        }
                        persons.Add(person);
                    }
                }
            }

            if (persons.Count > 0)
            {
                _personResults = persons;
                lstResults.Items.Clear();

                foreach (var person in persons)
                {
                    string name = $"{person["first_name"]} {person["last_name"]}";
                    if (person.ContainsKey("employer_name") && person["employer_name"] != DBNull.Value)
                        name += $" - {person["employer_name"]}";
                    lstResults.Items.Add(name);
                }

                // Select first result
                lstResults.SelectedIndex = 0;
            }
            else
            {
                MessageBox.Show("No matching persons found", "NOT FOUND",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                ClearResults();
            }
        }

        private void LoadPersonById(int pedId)
        {
            using (var cmd = new SQLiteCommand(@"
                SELECT 
                    p.*,
                    e.company_id,
                    c.name as employer_name,
                    e.job_title
                FROM peds p
                LEFT JOIN employment e ON p.id = e.ped_id AND e.is_current = 1
                LEFT JOIN companies c ON e.company_id = c.id
                WHERE p.id = @id", _connection))
            {
                cmd.Parameters.AddWithValue("@id", pedId);

                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        _currentPerson = new Dictionary<string, object>();
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            _currentPerson[reader.GetName(i)] = reader.GetValue(i);
                        }
                        lblPersonName.Text = $"{_currentPerson["first_name"]} {_currentPerson["last_name"]}";

                        // Update status labels
                        bool isWanted = _currentPerson.ContainsKey("is_wanted") && Convert.ToBoolean(_currentPerson["is_wanted"]);
                        bool isIncarcerated = _currentPerson.ContainsKey("is_incarcerated") && Convert.ToBoolean(_currentPerson["is_incarcerated"]);

                        lblWantedStatus.Text = isWanted ? "⚠ WANTED" : "";
                        lblWantedStatus.ForeColor = isWanted ? Color.Red : Color.White;

                        lblIncarceratedStatus.Text = isIncarcerated ? "⛓ INCARCERATED" : "";
                        lblIncarceratedStatus.ForeColor = isIncarcerated ? Color.Red : Color.White;
                    }
                }
            }
        }

        private List<Dictionary<string, object>> GetPersonVehicles(int personId)
        {
            var vehicles = new List<Dictionary<string, object>>();

            using (var cmd = new SQLiteCommand(@"
                SELECT id, license_plate, vehicle_model, color_primary, color_secondary
                FROM vehicles 
                WHERE owner_type = 'person' AND owner_id = @personId AND is_active = 1", _connection))
            {
                cmd.Parameters.AddWithValue("@personId", personId);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var vehicle = new Dictionary<string, object>();
                        vehicle["id"] = reader["id"];
                        vehicle["license_plate"] = reader["license_plate"];
                        vehicle["vehicle_model"] = reader["vehicle_model"];
                        vehicle["color_primary"] = reader["color_primary"];
                        vehicle["color_secondary"] = reader["color_secondary"];
                        vehicles.Add(vehicle);
                    }
                }
            }

            return vehicles;
        }

        private void DisplayVehicleDetails()
        {
            if (_currentVehicle == null) return;

            txtDetails.Clear();

            AppendColoredText("╔════════════════════════════════════════════════════════════╗", Color.Cyan, true);
            AppendColoredText("║                     VEHICLE INFORMATION                    ║", Color.Cyan, true);
            AppendColoredText("╚════════════════════════════════════════════════════════════╝", Color.Cyan, true);
            AppendText("\r\n");

            AppendText($"Plate: {_currentVehicle["license_plate"]}\r\n");
            AppendText($"Model: {_currentVehicle["vehicle_model"]}\r\n");
            AppendText($"Color: {_currentVehicle["color_primary"]} / {_currentVehicle["color_secondary"]}\r\n");
            AppendText($"State: {_currentVehicle["registered_state"]}\r\n\r\n");

            // Registration & Insurance
            AppendColoredText("REGISTRATION & INSURANCE", Color.Yellow, true);
            AppendText("──────────────────────────────────────────────────\r\n");

            if (_currentVehicle.ContainsKey("no_registration") && Convert.ToInt32(_currentVehicle["no_registration"]) == 1)
            {
                AppendColoredText("NO REGISTRATION", Color.Red, true);
            }
            else if (_currentVehicle.ContainsKey("registration_expiry") && _currentVehicle["registration_expiry"] != DBNull.Value)
            {
                string expiry = _currentVehicle["registration_expiry"].ToString();
                if (DateTime.TryParse(expiry, out DateTime expDate))
                {
                    Color color = expDate < DateTime.Now ? Color.Red : Color.LightGreen;
                    AppendColoredText($"Registration: {expiry}", color, false);
                }
                else
                {
                    AppendText($"Registration: {expiry}\r\n");
                }
            }

            if (_currentVehicle.ContainsKey("no_insurance") && Convert.ToInt32(_currentVehicle["no_insurance"]) == 1)
            {
                AppendColoredText("NO INSURANCE", Color.Red, true);
            }
            else if (_currentVehicle.ContainsKey("insurance_expiry") && _currentVehicle["insurance_expiry"] != DBNull.Value)
            {
                string expiry = _currentVehicle["insurance_expiry"].ToString();
                if (DateTime.TryParse(expiry, out DateTime expDate))
                {
                    Color color = expDate < DateTime.Now ? Color.Red : Color.LightGreen;
                    AppendColoredText($"Insurance: {expiry}", color, false);
                }
                else
                {
                    AppendText($"Insurance: {expiry}\r\n");
                }
            }
            AppendText("\r\n");

            // Status Flags
            AppendColoredText("STATUS", Color.Orange, true);
            AppendText("──────────────────────────────────────────────────\r\n");

            if (_currentVehicle.ContainsKey("is_stolen") && Convert.ToInt32(_currentVehicle["is_stolen"]) == 1)
            {
                AppendColoredText("*** STOLEN VEHICLE ***", Color.Red, true);
                if (_currentVehicle.ContainsKey("stolen_reason"))
                    AppendText($"Reason: {_currentVehicle["stolen_reason"]}\r\n");
            }

            if (_currentVehicle.ContainsKey("is_impounded") && Convert.ToInt32(_currentVehicle["is_impounded"]) == 1)
            {
                AppendColoredText("*** IMPOUNDED ***", Color.Red, true);
                if (_currentVehicle.ContainsKey("impounded_reason"))
                    AppendText($"Reason: {_currentVehicle["impounded_reason"]}\r\n");
                if (_currentVehicle.ContainsKey("impounded_location"))
                    AppendText($"Location: {_currentVehicle["impounded_location"]}\r\n");
            }
            AppendText("\r\n");

            // Owner Information
            if (_currentVehicle.ContainsKey("owner_name") && _currentVehicle["owner_name"] != DBNull.Value)
            {
                AppendColoredText("OWNER INFORMATION", Color.Cyan, true);
                AppendText("──────────────────────────────────────────────────\r\n");
                AppendText($"Name: {_currentVehicle["owner_name"]}\r\n");

                if (_currentVehicle.ContainsKey("license_status") && _currentVehicle["license_status"] != DBNull.Value)
                {
                    string status = _currentVehicle["license_status"].ToString();
                    Color statusColor = status.ToUpper() == "VALID" ? Color.LightGreen : Color.Red;
                    AppendColoredText($"License: {status}", statusColor, false);
                }

                if (_currentVehicle.ContainsKey("is_wanted") && Convert.ToBoolean(_currentVehicle["is_wanted"]))
                {
                    AppendColoredText("*** OWNER IS WANTED ***", Color.Red, true);
                }

                if (_currentVehicle.ContainsKey("is_incarcerated") && Convert.ToBoolean(_currentVehicle["is_incarcerated"]))
                {
                    AppendColoredText("*** OWNER IS INCARCERATED ***", Color.Red, true);
                }
            }
        }

        private void DisplayPersonDetails()
        {
            if (_currentPerson == null) return;

            txtDetails.Clear();

            AppendColoredText("╔════════════════════════════════════════════════════════════╗", Color.Cyan, true);
            AppendColoredText("║                     PERSON INFORMATION                     ║", Color.Cyan, true);
            AppendColoredText("╚════════════════════════════════════════════════════════════╝", Color.Cyan, true);
            AppendText("\r\n");

            AppendText($"Name: {_currentPerson["first_name"]} {_currentPerson["last_name"]}\r\n");

            if (_currentPerson.ContainsKey("home_address") && _currentPerson["home_address"] != DBNull.Value)
                AppendText($"Address: {_currentPerson["home_address"]}\r\n");

            AppendText($"Model: {_currentPerson["model_name"]}\r\n");
            AppendText($"Gender: {_currentPerson["gender"]}\r\n");

            if (_currentPerson.ContainsKey("date_of_birth") && _currentPerson["date_of_birth"] != DBNull.Value)
                AppendText($"DOB: {_currentPerson["date_of_birth"]}\r\n");

            AppendText("\r\n");

            // License Information
            AppendColoredText("LICENSE INFORMATION", Color.Yellow, true);
            AppendText("──────────────────────────────────────────────────\r\n");
            AppendText($"Number: {_currentPerson["license_number"]}\r\n");

            string status = _currentPerson["license_status"].ToString();
            Color statusColor = Color.LightGreen;
            if (status.ToUpper() == "SUSPENDED") statusColor = Color.Orange;
            if (status.ToUpper() == "REVOKED" || status.ToUpper() == "EXPIRED" || status.ToUpper() == "NOLICENSE") statusColor = Color.Red;

            AppendColoredText($"Status: {status}", statusColor, false);

            if (_currentPerson.ContainsKey("license_reason") && _currentPerson["license_reason"] != DBNull.Value)
                AppendText($"Reason: {_currentPerson["license_reason"]}\r\n");

            if (_currentPerson.ContainsKey("license_expiry") && _currentPerson["license_expiry"] != DBNull.Value)
            {
                string expiry = _currentPerson["license_expiry"].ToString();
                if (DateTime.TryParse(expiry, out DateTime expDate))
                {
                    Color expColor = expDate < DateTime.Now ? Color.Red : Color.LightGreen;
                    AppendColoredText($"Expires: {expiry}", expColor, false);
                }
                else
                {
                    AppendText($"Expires: {expiry}\r\n");
                }
            }

            AppendText($"Class: {_currentPerson["license_class"]}\r\n\r\n");

            // Employment
            if (_currentPerson.ContainsKey("employer_name") && _currentPerson["employer_name"] != DBNull.Value)
            {
                AppendColoredText("EMPLOYMENT", Color.Cyan, true);
                AppendText("──────────────────────────────────────────────────\r\n");
                AppendText($"Employer: {_currentPerson["employer_name"]}\r\n");
                AppendText($"Job: {_currentPerson["job_title"]}\r\n\r\n");
            }

            // Status Flags
            AppendColoredText("STATUS", Color.Orange, true);
            AppendText("──────────────────────────────────────────────────\r\n");

            if (_currentPerson.ContainsKey("is_wanted") && Convert.ToBoolean(_currentPerson["is_wanted"]))
            {
                AppendColoredText("*** WANTED ***", Color.Red, true);
                if (_currentPerson.ContainsKey("wanted_reason") && _currentPerson["wanted_reason"] != DBNull.Value)
                    AppendText($"Reason: {_currentPerson["wanted_reason"]}\r\n");
            }

            if (_currentPerson.ContainsKey("is_incarcerated") && Convert.ToBoolean(_currentPerson["is_incarcerated"]))
            {
                AppendColoredText("*** INCARCERATED ***", Color.Red, true);
                if (_currentPerson.ContainsKey("incarcerated_reason") && _currentPerson["incarcerated_reason"] != DBNull.Value)
                    AppendText($"Reason: {_currentPerson["incarcerated_reason"]}\r\n");
                if (_currentPerson.ContainsKey("incarcerated_days") && _currentPerson["incarcerated_days"] != DBNull.Value)
                    AppendText($"Days: {_currentPerson["incarcerated_days"]}\r\n");
                if (_currentPerson.ContainsKey("release_date") && _currentPerson["release_date"] != DBNull.Value)
                    AppendText($"Release: {_currentPerson["release_date"]}\r\n");
            }
            AppendText("\r\n");

            // Owned Vehicles
            var vehicles = GetPersonVehicles(Convert.ToInt32(_currentPerson["id"]));
            if (vehicles.Count > 0)
            {
                AppendColoredText("OWNED VEHICLES", Color.Cyan, true);
                AppendText("──────────────────────────────────────────────────\r\n");
                foreach (var v in vehicles)
                {
                    string plate = v["license_plate"].ToString();
                    string model = v["vehicle_model"].ToString();
                    string colors = $"{v["color_primary"]}/{v["color_secondary"]}";
                    AppendText($"  • {plate}: {model} ({colors})\r\n");
                }
            }
        }

        private void AppendText(string text)
        {
            txtDetails.SelectionStart = txtDetails.TextLength;
            txtDetails.SelectionLength = 0;
            txtDetails.SelectionColor = Color.White;
            txtDetails.AppendText(text);
        }

        private void AppendColoredText(string text, Color color, bool bold)
        {
            txtDetails.SelectionStart = txtDetails.TextLength;
            txtDetails.SelectionLength = 0;
            txtDetails.SelectionColor = color;
            if (bold)
                txtDetails.SelectionFont = new Font(txtDetails.Font, FontStyle.Bold);
            txtDetails.AppendText(text + "\r\n");
            txtDetails.SelectionFont = new Font(txtDetails.Font, FontStyle.Regular);
        }

        private void LoadVehicleTickets(int vehicleId)
        {
            var dt = new System.Data.DataTable();

            using (var cmd = new SQLiteCommand(@"
                SELECT date_issued as 'Date', offense as 'Offense', fine_amount as 'Fine', location as 'Location'
                FROM tickets 
                WHERE vehicle_id = @vehicleId
                ORDER BY date_issued DESC", _connection))
            {
                cmd.Parameters.AddWithValue("@vehicleId", vehicleId);

                using (var adapter = new SQLiteDataAdapter(cmd))
                {
                    adapter.Fill(dt);
                }
            }

            gridTickets.DataSource = dt;
        }

        private void LoadPersonTickets(int personId)
        {
            var dt = new System.Data.DataTable();

            using (var cmd = new SQLiteCommand(@"
                SELECT t.date_issued as 'Date', t.offense as 'Offense', 
                       t.fine_amount as 'Fine', v.license_plate as 'Vehicle'
                FROM tickets t
                LEFT JOIN vehicles v ON t.vehicle_id = v.id
                WHERE t.ped_id = @personId
                ORDER BY t.date_issued DESC", _connection))
            {
                cmd.Parameters.AddWithValue("@personId", personId);

                using (var adapter = new SQLiteDataAdapter(cmd))
                {
                    adapter.Fill(dt);
                }
            }

            gridTickets.DataSource = dt;
        }

        private void BtnIssueTicket_Click(object sender, EventArgs e)
        {
            if (_connection == null)
            {
                MessageBox.Show("Please open a database first (File > Open Database)", "NO DATABASE",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (_currentPerson == null)
            {
                MessageBox.Show("Please search for a person first", "ERROR",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int personId = Convert.ToInt32(_currentPerson["id"]);
            string personName = $"{_currentPerson["first_name"]} {_currentPerson["last_name"]}";

            var vehicles = GetPersonVehicles(personId);

            // FIX: Ensure lists are not null
            if (_citations == null) _citations = new List<string>();
            if (_arrests == null) _arrests = new List<string>();

            var dialog = new IssueTicketDialog(personName, vehicles, _citations, _arrests);

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    using (var transaction = _connection.BeginTransaction())
                    {
                        // Insert ticket
                        string ticketSql = @"
                            INSERT INTO tickets (ped_id, vehicle_id, offense, fine_amount, issuing_officer, location, date_issued)
                            VALUES (@pedId, @vehicleId, @offense, @fine, 'External MDT', @location, @date)";

                        using (var cmd = new SQLiteCommand(ticketSql, _connection))
                        {
                            cmd.Parameters.AddWithValue("@pedId", personId);
                            cmd.Parameters.AddWithValue("@vehicleId", dialog.SelectedVehicleId);
                            cmd.Parameters.AddWithValue("@offense", dialog.SelectedOffense.Split('-')[1].Trim());
                            cmd.Parameters.AddWithValue("@fine", dialog.Fine);
                            cmd.Parameters.AddWithValue("@location", dialog.TicketLocation);
                            cmd.Parameters.AddWithValue("@date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                            cmd.ExecuteNonQuery();
                        }

                        // If arrest, update person record
                        if (dialog.IsArrest)
                        {
                            DateTime releaseDate = DateTime.Now.AddDays(dialog.JailDays);

                            string arrestSql = @"
                                UPDATE peds SET 
                                    is_incarcerated = 1,
                                    incarcerated_reason = @reason,
                                    incarcerated_date = @date,
                                    incarcerated_days = @days,
                                    release_date = @releaseDate
                                WHERE id = @pedId";

                            using (var cmd = new SQLiteCommand(arrestSql, _connection))
                            {
                                cmd.Parameters.AddWithValue("@pedId", personId);
                                cmd.Parameters.AddWithValue("@reason", dialog.SelectedOffense.Split('-')[1].Trim());
                                cmd.Parameters.AddWithValue("@date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                                cmd.Parameters.AddWithValue("@days", dialog.JailDays);
                                cmd.Parameters.AddWithValue("@releaseDate", releaseDate.ToString("yyyy-MM-dd HH:mm:ss"));
                                cmd.ExecuteNonQuery();
                            }

                            // Add to incarceration history
                            string historySql = @"
                                INSERT INTO incarceration_history (ped_id, reason, days_sentenced, date_incarcerated, notes)
                                VALUES (@pedId, @reason, @days, @date, 'Issued from External MDT')";

                            using (var cmd = new SQLiteCommand(historySql, _connection))
                            {
                                cmd.Parameters.AddWithValue("@pedId", personId);
                                cmd.Parameters.AddWithValue("@reason", dialog.SelectedOffense.Split('-')[1].Trim());
                                cmd.Parameters.AddWithValue("@days", dialog.JailDays);
                                cmd.Parameters.AddWithValue("@date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                                cmd.ExecuteNonQuery();
                            }
                        }

                        transaction.Commit();
                    }

                    string message = dialog.IsArrest ?
                        $"ARREST issued to {personName} - {dialog.JailDays} days" :
                        $"CITATION issued to {personName} - ${dialog.Fine}";

                    MessageBox.Show(message, "SUCCESS", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    // Refresh tickets
                    LoadPersonTickets(personId);
                    LoadStats();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error issuing ticket: {ex.Message}", "ERROR",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}