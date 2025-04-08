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
    public partial class frmDefine : Form
    {
        public frmDefine(List<server> servers)
        {
            InitializeComponent();

            foreach (server s in servers)
            {
                ListViewItem li = new ListViewItem();
                li.Text = s.fqdn;
                li.SubItems.Add(s.currentIP);
                li.SubItems.Add(s.newIP);
                listView1.Items.Add(li);
            }


        }

        private void addToolStripMenuItem_Click(object sender, EventArgs e)
        {
            frmAddEdit frm = new frmAddEdit();
            DialogResult result = frm.ShowDialog();

            if (result != DialogResult.OK) return;

            server s = (server)frm.Tag;

            ListViewItem li = new ListViewItem();
            li.Text = s.fqdn;
            li.SubItems.Add(s.currentIP);
            li.SubItems.Add(s.newIP);
            listView1.Items.Add(li);

        }

        private void deleteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ListViewItem li in listView1.SelectedItems)
            {
                li.Remove();
            }
        }

        private void editToolStripMenuItem_Click(object sender, EventArgs e)
        {

            frmAddEdit frm = new frmAddEdit(listView1.SelectedItems[0].Text, listView1.SelectedItems[0].SubItems[1].Text, listView1.SelectedItems[0].SubItems[2].Text);
            DialogResult result = frm.ShowDialog();

            if (result != DialogResult.OK) return;

            server s = (server)frm.Tag;

            listView1.SelectedItems[0].Text = s.fqdn;
            listView1.SelectedItems[0].SubItems[1].Text = s.currentIP;
            listView1.SelectedItems[0].SubItems[2].Text = s.newIP;

        }

        private void cmdOK_Click(object sender, EventArgs e)
        {
            List<server> servers = new List<server>();

            foreach (ListViewItem li in listView1.Items)
            {
                server s = new server();
                s.fqdn = li.Text.Trim().ToLower();                                          // dc01.myad.local
                s.hostname = s.fqdn.Substring(0, s.fqdn.IndexOf(".")).Trim().ToLower();     // dc01
                s.domainDNS = s.fqdn.Replace(s.hostname + ".", "");                         // myad.local
                s.currentIP = li.SubItems[1].Text.Trim();
                s.newIP = li.SubItems[2].Text.Trim();

                servers.Add(s);


            }

            this.DialogResult = DialogResult.OK;
            this.Tag = servers;
            this.Close();
            return;


        }

        private void cmdCancel_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
            return;
        }

        private void frmDefine_Load(object sender, EventArgs e)
        {

        }

       

        private void loadToolStripMenuItem_Click(object sender, EventArgs e)
        {
            openFileDialog1.FileName = "servers.txt";
            DialogResult result = openFileDialog1.ShowDialog();

            if (result != DialogResult.OK) return;

            string[] lines = System.IO.File.ReadAllLines(openFileDialog1.FileName);

            try
            {

                foreach (string line in lines)
                {
                    if (line.Trim() == "" || line.StartsWith("#")) continue;

                    string[] tmp = line.Split(',');

                    ListViewItem li = new ListViewItem();
                    li.Text = tmp[0];
                    li.SubItems.Add(tmp[1]);
                    li.SubItems.Add(tmp[2]);
                    listView1.Items.Add(li);

                }
            }
            catch
            {
                MessageBox.Show("Invalid format");
            }

        }

        private void saveToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string s = "";
            foreach (ListViewItem li in listView1.Items)
            {
                s += $"{li.Text},{li.SubItems[1].Text},{li.SubItems[2].Text}" + "\r\n";
            }

            saveFileDialog1.FileName = "servers.txt";
            DialogResult result = saveFileDialog1.ShowDialog();

            if (result != DialogResult.OK) return;

            using (System.IO.StreamWriter myFileHandle = new System.IO.StreamWriter(saveFileDialog1.FileName, false))
            {
                myFileHandle.WriteLine(s);
            }

        }
    }
}
