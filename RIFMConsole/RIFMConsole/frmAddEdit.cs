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
    public partial class frmAddEdit : Form
    {
        public frmAddEdit()
        {
            InitializeComponent();
        }

        public frmAddEdit(string fqdn,string currentIP,string newIP)
        {
            InitializeComponent();
            txtFQDN.Text = fqdn;
            txtCurrentIP.Text = currentIP;
            txtNewIP.Text = newIP;
        }

        private void cmdOK_Click(object sender, EventArgs e)
        {
            server s = new server();

            s.fqdn = txtFQDN.Text;
            s.currentIP = txtCurrentIP.Text;
            s.newIP = txtNewIP.Text;

            this.DialogResult = DialogResult.OK;
            this.Tag = s;
            this.Close();
            return;
        }

        private void cmdClose_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
            return;
        }

        private void frmAddEdit_Load(object sender, EventArgs e)
        {

        }
    }
}
