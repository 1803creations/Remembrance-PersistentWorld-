using System;
using System.Drawing;
using System.Windows.Forms;

namespace PersistentWorldAdmin
{
    public class IncarcerateForm : Form
    {
        public string Reason { get; private set; }
        public int Days { get; private set; }
        public string Notes { get; private set; }

        private TextBox txtReason;
        private NumericUpDown numDays;
        private TextBox txtNotes;
        private Button btnOK;
        private Button btnCancel;

        public IncarcerateForm(string personName)
        {
            InitializeComponent(personName);
        }

        private void InitializeComponent(string personName)
        {
            this.Text = $"Incarcerate {personName}";
            this.Size = new Size(400, 250);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            int yPos = 20;
            int labelWidth = 80;
            int controlWidth = 250;

            // Reason
            var lblReason = new Label { Text = "Reason:", Location = new Point(20, yPos), Size = new Size(labelWidth, 25) };
            txtReason = new TextBox { Location = new Point(110, yPos), Size = new Size(controlWidth, 25) };
            Controls.Add(lblReason);
            Controls.Add(txtReason);
            yPos += 35;

            // Days
            var lblDays = new Label { Text = "Days:", Location = new Point(20, yPos), Size = new Size(labelWidth, 25) };
            numDays = new NumericUpDown { Location = new Point(110, yPos), Size = new Size(100, 25), Minimum = 1, Maximum = 9999, Value = 30 };
            Controls.Add(lblDays);
            Controls.Add(numDays);
            yPos += 35;

            // Notes
            var lblNotes = new Label { Text = "Notes:", Location = new Point(20, yPos), Size = new Size(labelWidth, 25) };
            txtNotes = new TextBox { Location = new Point(110, yPos), Size = new Size(controlWidth, 25) };
            Controls.Add(lblNotes);
            Controls.Add(txtNotes);
            yPos += 45;

            // Buttons
            btnOK = new Button { Text = "Incarcerate", Location = new Point(110, yPos), Size = new Size(100, 30), BackColor = Color.LightCoral };
            btnOK.Click += BtnOK_Click;

            btnCancel = new Button { Text = "Cancel", Location = new Point(220, yPos), Size = new Size(100, 30) };
            btnCancel.Click += (s, e) => DialogResult = DialogResult.Cancel;

            Controls.Add(btnOK);
            Controls.Add(btnCancel);
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtReason.Text))
            {
                MessageBox.Show("Please enter a reason", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Reason = txtReason.Text;
            Days = (int)numDays.Value;
            Notes = txtNotes.Text;

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}