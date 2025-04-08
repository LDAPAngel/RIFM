using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RIFMConsole
{
    public partial class frmSettings : Form
    {

        Settings settings;

        public frmSettings(Settings settings)
        {
            InitializeComponent();
            this.settings = settings;
        }

        private void frmSettings_Load(object sender, EventArgs e)
        {
            txtCommonEpoch.Text = settings.commonEpoch.ToString();
            txtDSRMPassword.Text = settings.dsrmPassword;
            txtUDPPort.Text = settings.udpPort.ToString();
        }

        private void cmdOK_Click(object sender, EventArgs e)
        {
            settings.udpPort = Int32.Parse(txtUDPPort.Text);
            settings.commonEpoch = Int32.Parse(txtCommonEpoch.Text);
            settings.dsrmPassword = txtDSRMPassword.Text;

            this.DialogResult = DialogResult.OK;
            this.Tag = settings;
            this.Close();
            return;
        }

        private void cmdClose_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
            return;

        }
    }
}
