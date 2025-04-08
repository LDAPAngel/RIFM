using Microsoft.Win32;
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
    public partial class frmCredentials : Form
    {
        string localOrAD;

        public frmCredentials()
        {
            InitializeComponent();


        }

        private void cmdOK_Click(object sender, EventArgs e)
        {
            Credentials credentials = new Credentials();
                                  
            credentials.localUsername = txtLocalUsername.Text;
            credentials.localPassword = txtLocalPassword.Text;

            this.Tag = credentials;
            this.DialogResult = DialogResult.OK;
            this.Close();
            return;

        }

        private void cmdCancel_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
            return;
        }

        private void frmCredentials_Load(object sender, EventArgs e)
        {
           
        }
    }
}
