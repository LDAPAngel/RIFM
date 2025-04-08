using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RIFMConsole
{
    public partial class frmMain : Form
    {
        Credentials credentials = new Credentials();
        Settings settings = new Settings();
        List<server> servers = new List<server>();
        List<int> uniqueEpochs = new List<int>();
        private Socket serverSocket = null;
        private byte[] byteData = new byte[1024];
        bool listening = false;
        private bool exitConsole;
        private bool recoveryComplete = false;


        public frmMain()
        {
            InitializeComponent();
        }

        private void frmMain_Load(object sender, EventArgs e)
        {
            //cmdResume.Visible = true;

            cmdDeploy.Enabled = false;
            cmdStart.Enabled = false;

            tabControl1.DrawMode = System.Windows.Forms.TabDrawMode.OwnerDrawFixed;

            tabControl1.Visible = false;
            this.Show();

        }

        private bool ListenForUDP()
        {
            if (listening) return true;

            try
            {
                this.serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                this.serverSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
                this.serverSocket.Bind(new IPEndPoint(IPAddress.Any, settings.udpPort));
                EndPoint newClientEP = new IPEndPoint(IPAddress.Any, 0);
                this.serverSocket.BeginReceiveFrom(this.byteData, 0, this.byteData.Length, SocketFlags.None, ref newClientEP, ProcessAgentResponse, newClientEP);

                listening = true;
                return true;
            }
            catch
            {
                MessageBox.Show($"Could not gain exclusive access to port {settings.udpPort}");
                return false;
            }

        }

        private void ProcessAgentResponse(IAsyncResult iar)
        {
            IPEndPoint ipe = null;

            try
            {
                EndPoint clientEP = new IPEndPoint(IPAddress.Any, 0);
                int dataLen = 0;
                byte[] data = null;
                try
                {
                    dataLen = this.serverSocket.EndReceiveFrom(iar, ref clientEP);
                    data = new byte[dataLen];
                    Array.Copy(this.byteData, data, dataLen);

                    ipe = (IPEndPoint)clientEP;
                }
                catch
                {
                }
                finally
                {
                    EndPoint newClientEP = new IPEndPoint(IPAddress.Any, 0);
                    this.serverSocket.BeginReceiveFrom(this.byteData, 0, this.byteData.Length, SocketFlags.None, ref newClientEP, ProcessAgentResponse, newClientEP);
                }


                string clientIP = ipe.Address.ToString();
                string responseFromAgent = System.Text.Encoding.UTF8.GetString(data, 0, data.Length);

                // agent will send status messages, these are not displayed but used to update 
                // servers list on status of each server during restore

                switch (responseFromAgent)
                {
                    case "ConsoleStatus:Failed":      // generic failed
                        {
                            server s = servers.Find(x => x.newIP == clientIP);
                            s.Failed = true;
                        }

                        return;

                    case "ConsoleStatus:ADDSFailed":
                        {
                            server s = servers.Find(x => x.newIP == clientIP);
                            s.Failed = true;
                            s.ADDSFailed = true;
                        }

                        return;

                    case "ConsoleStatus:PromoFailed":
                        {
                            server s = servers.Find(x => x.newIP == clientIP);
                            s.Failed = true;
                            s.PromoFailed = true;
                        }
                        return;

                    case "ConsoleStatus:PromoCompleted":
                        {
                            server s = servers.Find(x => x.newIP == clientIP);
                            s.PromoCompleted = true;
                        }
                        return;

                    case "ConsoleStatus:RenameFailed":
                        {
                            server s = servers.Find(x => x.newIP == clientIP);
                            s.Failed = true;
                            s.RenameFailed = true;
                        }
                        return;

                    case "ConsoleStatus:RenameCompleted":
                        {
                            server s = servers.Find(x => x.newIP == clientIP);
                            s.RenameCompleted = true;
                        }
                        return;



                    case "ConsoleStatus:DatabaseOperationsCompleted":
                        {
                            server s = servers.Find(x => x.newIP == clientIP);
                            s.DatabaseOperationsCompleted = true;
                        }
                        return;


                    case "ConsoleStatus:WaitingForRid":
                        {
                            server s = servers.Find(x => x.newIP == clientIP);
                            s.WaitingForRid = true;
                        }
                        return;



                    case "ConsoleStatus:RIDCompleted":
                        {
                            server s = servers.Find(x => x.newIP == clientIP);
                            s.RIDCompleted = true;
                        }
                        return;

                    case "ConsoleStatus:RestoreCompleted":
                        {
                            server s = servers.Find(x => x.newIP == clientIP);
                            s.RestoreCompleted = true;
                        }
                        return;

                    case "ConsoleStatus:Completed":
                        {
                            server s = servers.Find(x => x.newIP == clientIP);
                            s.Completed = true;
                        }
                        return;

                }



                // find which listview to add to
                Control[] c = this.Controls.Find(clientIP, true);
                ListView lv = c[0] as ListView;

                lv.Invoke((Action)(() =>
                {
                    ListViewItem li = new ListViewItem();
                    li.Text = responseFromAgent;
                    li.ImageKey = "status";

                    if (responseFromAgent.Contains("ERROR:"))
                    {
                        li.ForeColor = Color.Red;
                    }
                    else if (responseFromAgent.Contains("WARN:"))
                    {
                        li.ForeColor = Color.Orange;
                    }
                    else if (responseFromAgent.ToLower().Contains("rebooting"))
                    {
                        li.ImageKey = "reboot";
                    }
                    else if (responseFromAgent.Contains("Waiting for all restores to complete"))
                    {
                        server s = servers.Find(x => x.newIP == clientIP);
                        ChangeTabColour(s.hostname, Color.LightSalmon);
                    }
                    else if (responseFromAgent.Contains("Replication Completed"))
                    {
                        server s = servers.Find(x => x.newIP == clientIP);
                        ChangeTabColour(s.hostname, Color.LightGreen);
                    }
                    else if (responseFromAgent.Contains("Failed !!"))
                    {
                        server s = servers.Find(x => x.newIP == clientIP);
                        ChangeTabColour(s.hostname, Color.Red);
                        AbortRestore();
                    }

                    lv.Items.Add(li);
                    if (chkAutoScroll.Checked)
                    {
                        lv.Items[lv.Items.Count - 1].EnsureVisible();
                    }


                }));


            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void cmdDefine_Click(object sender, EventArgs e)
        {
            frmDefine frm = new frmDefine(servers);
            DialogResult result = frm.ShowDialog();

            if (result == DialogResult.OK)
            {
                this.servers = (List<server>)frm.Tag;
                if (this.servers.Count > 0)
                {
                    CreateTabs();
                }
            }

            cmdDeploy.Enabled = true;
            this.tabControl1.SelectedIndexChanged += new System.EventHandler(this.tabControl1_SelectedIndexChanged);

            if (!ListenForUDP())
            {
                MessageBox.Show($"There is another console session running using port {settings.udpPort}");
                return;
            }

        }

        private void CreateTabs()
        {

            tabControl1.TabPages.Clear();
            tabControl1.Visible = true;

            foreach (server s in servers)
            {

                tabControl1.TabPages.Add(s.hostname, s.hostname);
                tabControl1.TabPages[tabControl1.TabPages.Count - 1].Tag = s.newIP;


                // add a listview in each tab
                ListView listView = new System.Windows.Forms.ListView();
                listView.HideSelection = false;
                listView.Location = new System.Drawing.Point(0, 0);
                listView.Name = s.newIP;
                listView.Dock = DockStyle.Fill;
                listView.UseCompatibleStateImageBehavior = false;
                listView.View = System.Windows.Forms.View.Details;
                listView.Columns.Add($"Status for {s.fqdn} [{s.newIP}]", 2000);
                listView.SmallImageList = imageList1;
                tabControl1.TabPages[s.hostname].Controls.Add(listView);

                SetDoubleBuffering(listView, true);
            }

        }

        private void cmdDeploy_Click(object sender, EventArgs e)
        {
            frmCredentials frm = new frmCredentials();
            DialogResult result = frm.ShowDialog();

            if (result != DialogResult.OK) return;


            credentials = (Credentials)frm.Tag;

            cmdSettings.Enabled = false;

            foreach (server s in servers)
            {
                tabControl1.SelectedTab = tabControl1.TabPages[s.hostname];

                bool deployed = CopyAgentAndSupportingFiles(s.newIP);

                if (!deployed)
                {
                    return;
                }

                string osVersion = GetOSVersion(s.newIP, credentials.localUsername, credentials.localPassword);

                if (osVersion != "")
                {
                    Control[] c = this.Controls.Find(s.newIP, true);
                    ListView lv = c[0] as ListView;

                    if (!lv.Columns[0].Text.Contains(osVersion))
                    {
                        lv.Columns[0].Text += " " + osVersion;
                    }
                }

            }

            // show first tab 
            tabControl1.SelectedTab = tabControl1.TabPages[servers[0].hostname];

            cmdDefine.Enabled = false;
            cmdDeploy.Enabled = false;
            cmdStart.Enabled = true;


        }

        private bool CopyAgentAndSupportingFiles(string newServer)
        {
            string serversConfig = "";
            string executionCommand = "";


            foreach (server s in servers)
            {
                serversConfig += $"{s.fqdn},{s.currentIP},{s.newIP}\r\n";
            }


            executionCommand += @"sc create _RestoreFromIFM binPath=c:\RestoreFromIFM\RIFMSvc.exe start=auto" + "\r\n";
            executionCommand += @"reg add HKLM\Software\RestoreFromIFM /v NextStep /t REG_SZ /d Step1 /f" + "\r\n";
            executionCommand += @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe ""Set-NetFirewallProfile -Profile Domain,Public,Private -Enabled False""" + "\r\n";
            executionCommand += @"net stop  _RestoreFromIFM" + "\r\n";
            executionCommand += @"net start _RestoreFromIFM" + "\r\n";

            #region Create Settings.xml
            settings.isolationEpoch = GenerateEpoch();
            settings.remoteIP = GetLocalIPAdddress();

#if DEBUG
            settings.remoteIP = "192.168.4.223";
#endif

            System.Xml.Serialization.XmlSerializer xmlSerializer = new System.Xml.Serialization.XmlSerializer(settings.GetType());
            System.IO.StreamWriter myFile = new System.IO.StreamWriter(@"settings.xml", false);
            xmlSerializer.Serialize(myFile, settings);
            myFile.Close();
            #endregion

            try
            {
                string sourceDirectory = AppDomain.CurrentDomain.BaseDirectory;

                NetworkShare.DisconnectFromShare($@"\\{newServer}\c$", true);      // Disconnect from the server.
                NetworkShare.DisconnectFromShare($@"\\{newServer}\ipc$", true);    // also make sure no IPC$ connection as this can cause a credential conflict

                string mapShareError = null;

                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        mapShareError = NetworkShare.ConnectToShare($@"\\{newServer}\c$", credentials.localUsername, credentials.localPassword);

                        if (mapShareError == null)
                        {
                            Log(newServer, "Mapped network share");
                            break;
                        }
                    }
                    catch (Exception err)
                    {
                        MessageBox.Show($@"Error mapping to {newServer}\c$ {err.Message.Replace("\r\n", "")}");
                    }

                    Thread.Sleep(1000);
                }

                if (mapShareError != null)
                {
                    Log(newServer, $"Cannot map network drive to {newServer}", Color.Red);
                    return false;
                }

                Directory.CreateDirectory($@"\\{newServer}\c$\RestoreFromIFM");
                Log(newServer, $@"Created RestoreFromIFM directory on \\{newServer}\c$");

                using (System.IO.StreamWriter myFileHandle = new System.IO.StreamWriter($@"\\{newServer}\c$\RestoreFromIFM\servers.txt", false))
                {
                    myFileHandle.WriteLine(serversConfig);
                }
                Log(newServer, $@"Copied {sourceDirectory}\servers.txt to \\{newServer}\c$\RestoreFromIFM\servers.txt");


                using (System.IO.StreamWriter myFileHandle = new System.IO.StreamWriter($@"\\{newServer}\c$\RestoreFromIFM\execution.bat", false))
                {
                    myFileHandle.WriteLine(executionCommand);
                }
                Log(newServer, $@"Copied {sourceDirectory}\execution.bat to \\{newServer}\c$\RestoreFromIFM\execution.bat");




                CopyFiles(newServer, "settings.xml", sourceDirectory, $@"\\{newServer}\c$\RestoreFromIFM");
                CopyFiles(newServer, "RIFMSvc.exe", sourceDirectory, $@"\\{newServer}\c$\RestoreFromIFM");
                CopyFiles(newServer, "RIFMCore.dll", sourceDirectory, $@"\\{newServer}\c$\RestoreFromIFM");
                CopyFiles(newServer, "RIFMCore.pdb", sourceDirectory, $@"\\{newServer}\c$\RestoreFromIFM");


                // we need these files in the same directory as agent
                CopyFiles(newServer, "DSInternals.Common.dll", $@"{sourceDirectory}\DSInternals", $@"\\{newServer}\c$\RestoreFromIFM");
                CopyFiles(newServer, "DSInternals.DataStore.dll", $@"{sourceDirectory}\DSInternals", $@"\\{newServer}\c$\RestoreFromIFM");
                CopyFiles(newServer, "DSInternals.SAM.dll", $@"{sourceDirectory}\DSInternals", $@"\\{newServer}\c$\RestoreFromIFM");
                CopyFiles(newServer, "Esent.Interop.dll", $@"{sourceDirectory}\DSInternals", $@"\\{newServer}\c$\RestoreFromIFM");
                CopyFiles(newServer, "Esent.Isam.dll", $@"{sourceDirectory}\DSInternals", $@"\\{newServer}\c$\RestoreFromIFM");


                // all files in DSInternals need to be copied to C:\Windows\System32\WindowsPowerShell\v1.0\Modules\DSInternals on target server
                string targetDirectory = $@"\\{newServer}\c$\Windows\System32\WindowsPowerShell\v1.0\Modules\DSInternals";
                if (CopyFolderContents(newServer, $@"{sourceDirectory}\DSInternals", targetDirectory))
                {
                    Log(newServer, $@"Copied DSInternals directory to \\{newServer}\c$\Windows\System32\WindowsPowerShell\v1.0\Modules\DSInternals");
                }


                NetworkShare.DisconnectFromShare($@"\\{newServer}\c$", true);
                NetworkShare.DisconnectFromShare($@"\\{newServer}\ipc$", true);

                return true;

            }
            catch (Exception err)
            {
                MessageBox.Show($"[{newServer}] {err.Message.Replace("\r\n", "")}");
            }


            return false;


        }

        private bool CopyFiles(string newServer, string filename, string sourceDirectory, string targetDirectory)
        {
            try
            {
                File.Copy($@"{sourceDirectory}\{filename}", $@"{targetDirectory}\{filename}", true);
                Log(newServer, $@"Copied {sourceDirectory}\{filename} to {targetDirectory}");
                return true;
            }
            catch (Exception err)
            {
                Log(newServer, err.Message);
                return false;
            }
        }

        private bool CopyFolderContents(string newServer, string SourcePath, string DestinationPath)
        {
            SourcePath = SourcePath.EndsWith(@"\") ? SourcePath : SourcePath + @"\";
            DestinationPath = DestinationPath.EndsWith(@"\") ? DestinationPath : DestinationPath + @"\";

            try
            {
                if (Directory.Exists(SourcePath))
                {
                    if (Directory.Exists(DestinationPath) == false)
                    {
                        Directory.CreateDirectory(DestinationPath);
                    }

                    foreach (string files in Directory.GetFiles(SourcePath))
                    {
                        FileInfo fileInfo = new FileInfo(files);
                        fileInfo.CopyTo(DestinationPath + @"\" + fileInfo.Name, true);
                        //LogDeploy(newServer, $@"Copied {SourcePath}\{fileInfo.Name} to {DestinationPath}");   // uncomment to show each file copied
                    }

                    foreach (string drs in Directory.GetDirectories(SourcePath))
                    {
                        DirectoryInfo directoryInfo = new DirectoryInfo(drs);
                        if (CopyFolderContents(newServer, drs, DestinationPath + directoryInfo.Name) == false)
                        {
                            return false;
                        }
                    }
                }
                return true;
            }
            catch (Exception err)
            {
                Log(newServer, err.Message);
                return false;
            }
        }

        private void cmdStart_Click(object sender, EventArgs e)
        {
            cmdDefine.Enabled = false;
            cmdDeploy.Enabled = false;
            cmdStart.Enabled = false;
            cmdSettings.Enabled = false;
            cmdAbort.Enabled = true;

            foreach (server s in servers)
            {
                int processID = ExecuteProcess(s.newIP, credentials.localUsername, credentials.localPassword, $@"C:\RestoreFromIFM\execution.bat");

                for (int i = 0; i < 100; i++)
                {
                    Thread.Sleep(10);
                    Application.DoEvents();
                }
            }



            // wait until restores on all servers completed
            // also monitor when RIDs have completed and advise other domain controllers in same domain that their RID has completed




            while (true)
            {



                if (DateTime.Now.Second % 15 == 0)              // only check every 15 sec intervals
                {
                    bool allRestoresCompleted = true;

                    foreach (server s in servers)
                    {
                        if (!s.RestoreCompleted)
                        {
                            allRestoresCompleted = false;
                        }

                        if (s.RIDCompleted)
                        {
                            foreach (server ss in servers)
                            {
                                if (ss.domainDNS == s.domainDNS)
                                {
                                    SendToAgent(ss.newIP, "RID Completed");
                                }
                            }
                        }

                    }

                    if (allRestoresCompleted) break;
                }



                if (exitConsole) break;

                Application.DoEvents();
                Thread.Sleep(10);
            }

            if (exitConsole)
            {
                return;
            }

            // send command to each restored server to perform final steps
            // TODO: need to make sure server received as this is UDP !! Hack for now is to send 5 times

            for (int count = 0; count < 5; count++)
            {
                foreach (server s in servers)
                {
                    SendToAgent(s.newIP, "InitiateFinalSteps");

                    for (int i = 0; i < 10; i++)
                    {
                        Application.DoEvents();
                        Thread.Sleep(100);
                    }
                }
            }

            // wait until final steps on all servers completed
            while (true)
            {

                bool finalstepsCompleted = true;

                foreach (server s in servers)
                {
                    if (!s.Completed)
                    {
                        finalstepsCompleted = false;
                    }
                }

                if (finalstepsCompleted || exitConsole) break;

                Application.DoEvents();
                Thread.Sleep(10);
            }

            if (exitConsole) return;

            tabControl1.Refresh();

            cmdAbort.Enabled = false;

            // now do cleanup
            foreach (server s in servers)
            {
                SendToAgent(s.newIP, "Cleanup");
                Application.DoEvents();
            }

            for (int i = 0; i < 15; i++)    // wait 15 secs for agents to process
            {
                Thread.Sleep(1000);
                Application.DoEvents();
            }

            SaveLogs();

            lblCompleted.Visible = true;

            foreach (server s in servers)
            {
                Log(s.newIP, "Recovery completed !", Color.White, Color.Green);
            }

            recoveryComplete = true;
        }

        private void SendToAgent(string remoteIP, string s)
        {
            UdpClient udpClient = new UdpClient();

            Byte[] sendBytes = Encoding.ASCII.GetBytes(s);
            try
            {
                udpClient.Send(sendBytes, sendBytes.Length, remoteIP, settings.udpPort);
                Thread.Sleep(100);      // wait 100ms so that traffic is sent to RIFM Monitor, this ensures that the line sent will appear in order it was sent
            }
            catch
            {

            }
        }

        private void SaveLogs()
        {

            foreach (TabPage tabPage in tabControl1.TabPages)
            {
                string hostname = tabPage.Name;
                string ip = (string)tabPage.Tag;

                Control[] c = this.Controls.Find(ip, true);
                ListView lv = c[0] as ListView;

                using (System.IO.StreamWriter myFileHandle = new System.IO.StreamWriter($"RestoreFromIFMLog-{hostname}-{ip}.txt", false))
                {
                    foreach (ListViewItem li in lv.Items)
                    {
                        myFileHandle.WriteLine(li.Text);
                    }
                }
            }

        }

        private int ExecuteProcess(string server, string username, string password, string toExecute)
        {
            string _username = username;
            if (!username.Contains("\\"))
            {
                _username = server + "\\" + username;
            }



            ManagementScope managementScope = null;
            try
            {
                ConnectionOptions connectionOptions = new ConnectionOptions(@"en-US", $@"{_username}", password, null, ImpersonationLevel.Impersonate, AuthenticationLevel.Default, false, null, TimeSpan.FromSeconds(5));

                managementScope = new ManagementScope(new ManagementPath
                {
                    Server = server,
                    NamespacePath = @"root\cimv2"
                }, connectionOptions);


                managementScope.Connect();

            }
            catch
            {
                return 0;
            }


            // Execute process

            ObjectGetOptions objectGetOptions = new ObjectGetOptions();
            ManagementPath managementPath = new ManagementPath("Win32_Process");
            ManagementClass processClass = new ManagementClass(managementScope, managementPath, objectGetOptions);
            ManagementBaseObject inParams = processClass.GetMethodParameters("Create");
            inParams["CommandLine"] = toExecute;
            ManagementBaseObject outParams = processClass.InvokeMethod("Create", inParams, null);
            int retValue = int.Parse(outParams["returnValue"].ToString());
            int processId = int.Parse(outParams["processId"].ToString());

            if (retValue == 0)
            {
                // Logging.WriteToLogFile($"[{server}] Executing process  {toExecute}  (PID:{processId})", LogLevels.Debug);
                return processId;
            }
            else
            {
                // Logging.WriteToLogFile($"[{server}] Executing process  {toExecute}  failed", true);
                return 0;
            }


        }

        private void Log(string newIP, string s)
        {

            Control[] c = this.Controls.Find(newIP, true);

            ListView lv = c[0] as ListView;

            ListViewItem li = new ListViewItem();
            li.Text = $"{DateTime.UtcNow} {s}";
            li.ImageKey = "deploy";

            lv.Items.Add(li);

            Application.DoEvents();
        }

        private void Log(string newIP, string s, Color foreColour)
        {

            Control[] c = this.Controls.Find(newIP, true);

            ListView lv = c[0] as ListView;

            ListViewItem li = new ListViewItem();
            li.Text = $"{DateTime.UtcNow} {s}";
            li.ForeColor = foreColour;
            li.ImageKey = "deploy";

            lv.Items.Add(li);

            Application.DoEvents();
        }

        private void Log(string newIP, string s, Color foreColour, Color backColour)
        {

            Control[] c = this.Controls.Find(newIP, true);

            ListView lv = c[0] as ListView;

            ListViewItem li = new ListViewItem();
            li.Text = $"{DateTime.UtcNow} {s}";
            li.ForeColor = foreColour;
            li.BackColor = backColour;
            li.ImageKey = "deploy";

            lv.Items.Add(li);

            Application.DoEvents();
        }

        private string GetLocalIPAdddress()
        {

            List<string> ipaddresses = new List<string>();


            ManagementScope managementScope = new ManagementScope(@"\\.\ROOT\cimv2");

            if (managementScope == null) return null;

            SelectQuery query = new SelectQuery("Win32_NetworkAdapterConfiguration", "IPEnabled='True'");

            ManagementObjectSearcher mgmtSrchr = new ManagementObjectSearcher(managementScope, query);

            ManagementObjectCollection managementObjectCollection = mgmtSrchr.Get();

            foreach (ManagementObject result in managementObjectCollection)
            {


                string[] IPAddresses = (string[])result["IPAddress"];

                if (IPAddresses != null)
                {
                    foreach (string ipAddress in IPAddresses)
                    {
                        if (ipAddress.Contains("."))
                        {
                            return ipAddress;
                        }

                    }
                }
            }

            return null;
        }

        private int GenerateEpoch()
        {
            Random rnd = new Random();
            int newEpoch = rnd.Next(1, 10000);

            if (uniqueEpochs.Contains(newEpoch) || newEpoch == settings.commonEpoch)
            {
                GenerateEpoch();
            }

            uniqueEpochs.Add(newEpoch);
            return newEpoch;
        }

        private void cmdResume_Click(object sender, EventArgs e)
        {
            string ip = (string)tabControl1.SelectedTab.Tag;

            SendToAgent(ip, "resumeprocessing");
        }

        private void tabControl1_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                string ip = (string)tabControl1.SelectedTab.Tag;

                // find which listview to add to

                Control[] c = this.Controls.Find(ip, true);

                ListView lv = c[0] as ListView;

                if (lv.Items.Count > 0 && chkAutoScroll.Checked)
                {
                    lv.Items[lv.Items.Count - 1].EnsureVisible();
                }
            }
            catch
            {

            }
        }

        private void SetDoubleBuffering(System.Windows.Forms.Control control, bool value)
        {
            System.Reflection.PropertyInfo controlProperty = typeof(System.Windows.Forms.Control)
                .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            controlProperty.SetValue(control, value, null);
        }

        private void frmMain_FormClosing(object sender, FormClosingEventArgs e)
        {

            if (!recoveryComplete)
            {
                DialogResult result = MessageBox.Show("Recovery has not completed, are you sure ?", "Close", MessageBoxButtons.YesNo);
                if (result != DialogResult.Yes)
                {
                    e.Cancel = true;
                }
                else
                {
                    exitConsole = true;
                }
                return;
            }

            exitConsole = true;

        }

        private void tabControl1_DrawItem(object sender, DrawItemEventArgs e)
        {
            // e.Index is the index of the tab in the TabPages collection.
            e.Graphics.FillRectangle(new SolidBrush(servers[e.Index].colour), e.Bounds);


            // Then draw the current tab button text 
            Rectangle paddedBounds = e.Bounds;
            paddedBounds.Inflate(-2, -2);
            e.Graphics.DrawString(tabControl1.TabPages[e.Index].Text, this.Font, SystemBrushes.InfoText, paddedBounds);
        }

        private void ChangeTabColour(string hostname, Color colour)
        {

            server s = servers.Find(x => x.hostname.ToLower() == hostname.ToLower());

            s.colour = colour;
            tabControl1.Refresh();

        }

        private void tabControl1_Selected(object sender, TabControlEventArgs e)
        {
            if (e.TabPageIndex < 0) return;

            for (int i = 0; i < tabControl1.TabPages.Count; i++)
            {
                tabControl1.TabPages[i].Text = tabControl1.TabPages[i].Text.Replace(">> ", "").Replace(" <<", "");
            }

            tabControl1.TabPages[e.TabPageIndex].Text = ">> " + tabControl1.TabPages[e.TabPageIndex].Text + " <<";
        }

        private void cmdSettings_Click(object sender, EventArgs e)
        {
            frmSettings frm = new frmSettings(settings);
            DialogResult result = frm.ShowDialog();

            if (result != DialogResult.OK) return;

            settings = (Settings)frm.Tag;
        }

        private void cmdAbort_Click(object sender, EventArgs e)
        {
            if (!recoveryComplete)
            {
                DialogResult result = MessageBox.Show("Are you sure ?", "Abort", MessageBoxButtons.YesNo);

                if (result != DialogResult.Yes) return;
            }


            AbortRestore();

        }

        private void AbortRestore()
        {
            //TODO: maybe rebooting ..wait for acknowledgement ?

            foreach (server s in servers)
            {
                if (!s.Completed)
                {
                    SendToAgent(s.newIP, "Abort");
                    ChangeTabColour(s.hostname, Color.Red);
                }
            }
        }

        private string GetOSVersion(string server, string localUsername, string localPassword)
        {
            string _localUsername = localUsername;
            if (!_localUsername.Contains("\\"))
            {
                _localUsername = server + "\\" + localUsername;
            }



            SelectQuery query = null;
            ManagementObjectSearcher mgmtSrchr = null;
            ManagementObjectCollection managementObjectCollection = null;
            ManagementScope managementScope = null;


            try
            {
                ConnectionOptions connectionOptions = new ConnectionOptions(@"en-US", $@"{_localUsername}", localPassword, null, ImpersonationLevel.Impersonate, AuthenticationLevel.Default, false, null, TimeSpan.FromSeconds(5));

                managementScope = new ManagementScope(new ManagementPath
                {
                    Server = server,
                    NamespacePath = @"root\cimv2"
                }, connectionOptions);


                managementScope.Connect();

            }
            catch (Exception err)
            {
                return "";
            }



            query = new SelectQuery("Win32_OperatingSystem");

            mgmtSrchr = new ManagementObjectSearcher(managementScope, query);

            managementObjectCollection = mgmtSrchr.Get();



            foreach (ManagementObject result in managementObjectCollection)
            {
                string operatingSystem = (string)result["Caption"];

                return operatingSystem.Replace("Microsoft Windows Server ", "Win ");
            }

            return null;
        }
    }
}
