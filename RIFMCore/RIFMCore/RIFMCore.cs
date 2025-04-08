using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using DSInternals.Common;
using DSInternals.DataStore;
using DSInternals.SAM;
using DSInternals.SAM.Interop;
using Microsoft.Win32;
using System.Diagnostics;
using System.Management;
using System.Threading;
using System.IO;
using System.Diagnostics.Eventing.Reader;
using System.ServiceProcess;
using System.Net.Sockets;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Security;
using System.Runtime.InteropServices;

namespace RIFM
{
   
    enum RebootOptions
    {
        Normal,
        EnableDSRM,
        DisableDSRM
    }


    //TODO: we disable firewall in execution, but maybe gpo's enable firewall ?

    public class RIFMCore
    {
        static Settings settings = new Settings();

        static Socket serverSocket = null;
        static byte[] byteData = new byte[1024];
        static bool listening = false;

        static byte[] OnlineBootKey;
        static byte[] IFMBootKey;


        static bool NTDSAvailable = false;

        static List<RestoredServer> RestoredServers = new List<RestoredServer>();

        static string localIPAddress = "";

        const string localHost = "127.0.0.1";

        static int dsaMainProcess = 0;

        static DitInfo ditInfo = new DitInfo();

        static RestoredForestInfo restoredForestInfo = new RestoredForestInfo();

        static string TempDA = "DATempRIFM";
        static string TempDAPassword = "&Y^TNg78UINU&*mnjkf";

        static bool initiateFinalSteps = false;

        static bool _paused = false;

        static bool firewallDisabled = false;

        static bool ridCompleted = false;

        static bool cleanup = false;

        static Dictionary<string, string> siteLinks = new Dictionary<string, string>();



        [DllImport("netjoin.dll", CharSet = CharSet.Unicode, SetLastError = true, ThrowOnUnmappableChar = true), SuppressUnmanagedCodeSecurity]
        public static extern int NetpSetComputerAccountPassword(string machineName, string domainController, string username, string password, IntPtr reserved);

        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
        static extern UInt32 DnsFlushResolverCache();


        public static void ProcessSteps()
        {
            try
            {

                string NextStep = GetNextStep();

                if (NextStep.ToLower() == "completed") return;

                if (NextStep.ToLower() == "failed") return;


                // these are always performed at every reboot
                {
                    GetLocalIPAdddress();

                    ReadSettings();

                    ReadServers();

                    GetDitInfo();

                    GetRestoredForestInfo();

                    //AllowConsoleAccess();

                    ListenForUDP();
                }



                switch (NextStep.ToLower())                         // which step to process after a reboot
                {

                    case "step1":

                        CheckIFM();
                        ShowDITInfo();
                        ShowRestoredForestInfo();
                        ShowDomainControllersToBeRestoredRemoved();

                        // now the process starts

                        DisableNetBios();                           // this allows promotion on multiple domain controllers at same time with same netbios domain name
                        StopService("wuauserv");
                        DisableService("wuauserv");
                        SetServiceTimeout();
                        RenameComputer();
                        SetNextStep("Step2");
                        Reboot(RebootOptions.Normal);               // reboot will happen even is a rename was not required
                        break;

                    case "step2":

                        SendToConsole("ConsoleStatus:RenameCompleted");
                        CheckAndPromote();
                        SendToConsole("ConsoleStatus:PromoCompleted");
                        DatabaseOperations();
                        SetNextStep("Step3");
                        Reboot(RebootOptions.Normal);

                        break;


                    case "step3":               
                        SendToConsole("ConsoleStatus:DatabaseOperationsCompleted");
                        IsolateDomainController();
                        MonitorFor1704Event();                                              // need to wait for ADDS to be available
                        DFSROperations();
                        PollDSNow();
                        SeizeFSMOs();
                        StopService("kdc", !restoredForestInfo.ThisDomainController.isRID);  // stop kdc on all except the rid master
                        FixDNS();
                        ConfigureDNSSettings();
                        RestartService("DNS");
                        FlushDNSResolverCache();
                        MetaDataCleanup();
                        ChangeSiteLinkOptions();
                        MakeDFSRAuthoratativeOnPDC();
                        FlushDNSResolverCache();
                        

                        // for some reason yet not determined, replicateSingleObject fails when trying to replicate RID computer account
                        // disabling kdc seems to bypass issue
                        if (ditInfo.OSName.Contains("2025") && !restoredForestInfo.ThisDomainController.isRID)
                        {
                            SetNextStep("Step4");
                            DisableService("kdc");
                            Reboot(RebootOptions.Normal);
                        }

                        break;

                    case "step4":

                        MonitorFor1704Event();
                        break;
                }


                if (restoredForestInfo.ThisDomainController.isRID)
                {
                    // steps performed on RID master only
                    RaiseRIDPool();
                    InvalidateRidPool();
                    CreateTempAdminAccount();
                    ResetComputerPassword(restoredForestInfo.RIDMaster.dnsHostName);
                    SetReplicationEpochInAD();
                    SendToConsole("ConsoleStatus:RIDCompleted");
                }
                else
                {
                    // steps performed on other domain domain controllers
                    WaitForRIDToComplete();


                    for (int i = 0; i < 3; i++)
                    {
                        PurgeTickets();
                        ResetComputerPassword(restoredForestInfo.dnsHostName);
                        SetReplicationEpochInAD();
                        bool result = ReplicateSingleObject($"{restoredForestInfo.RIDMaster.dsServiceName}", restoredForestInfo.RIDMaster.serverReference);
                        if (result == true) break;
                    }

                }


                SendToConsole("ConsoleStatus:RestoreCompleted");
                Log($"{restoredForestInfo.dnsHostName} has completed restore");
                Log("Waiting for all restores to complete");




                // restore has finished on this domain controller
                // now need to wait until ALL domain controllers have been restored
                // and then will force a full replication on each domain controller
                // wait until console has sent the final steps to start
                while (!initiateFinalSteps)
                {
                    Thread.Sleep(10);
                }

                Log($"All domain controllers have completed restore, executing final steps");

                #region Final Steps

                if (ditInfo.OSName.Contains("2025") && !restoredForestInfo.ThisDomainController.isRID)
                {
                    EnableService("kdc");
                }


                RestartService("DFSR");
                FlushDNSResolverCache();
                FlushDNSServerCache();
                StartService("kdc");
                InvalidateRidPool();
                ReplicateDomainControllerAccounts();
                ReplicateFSMORoles();
                DisableGC();
                RunKCC(restoredForestInfo.dnsHostName);
                ForceReplication();
                WaitUntilReplicationCompleted();
                SendToConsole("ConsoleStatus:Completed");
                Log("Replication Completed");

                #endregion

                while (!cleanup)
                {
                    Thread.Sleep(10);
                }

                CleanUp();

                SetNextStep("Completed");




            }
            catch (Exception err)
            {
                Log(err.Message);

                using (System.IO.StreamWriter myFileHandle = new System.IO.StreamWriter(@"C:\RestoreFromIFM\Errors.txt", false))
                {
                    myFileHandle.WriteLine(err.Message);
                    myFileHandle.WriteLine(err.StackTrace);
                }

                StopService("_RestoreFromIFM");
            }


        }

        static void RenameComputer()
        {
            if (Environment.MachineName.ToLower() != ditInfo.HostName)
            {
                int renameReturnCode = RenameComputer(ditInfo.HostName);

                switch (renameReturnCode)
                {
                    case -1:
                        Log($"ERROR:: occured during rename of computer");
                        StepFailed("ConsoleStatus:RenameFailed");
                        break;

                    case -2:
                        Log("Rename of computer not required");
                        break;

                    case 0:
                        // rename worked
                        break;

                    default:
                        Log($"ERROR:: occured during rename of computer {renameReturnCode}");
                        StepFailed("ConsoleStatus:RenameFailed");
                        break;

                }
            }
        }

        //static int PromotePS()
        //{

        //    string dsrmPasswordLine = "";
        //    string featuresLine = "";
        //    string promoteLine = "";

        //    string databasePath = @"C:\Windows\NTDS";
        //    string logsPath = databasePath;
        //    string sysvolPath = @"C:\Windows\SYSVOL";



        //    dsrmPasswordLine = $"$dsrmPassword = ConvertTo-SecureString 'P@ssword' -AsPlainText -Force;";
        //    featuresLine = "Add-WindowsFeature AD-Domain-Services, RSAT-AD-PowerShell, RSAT-ADDS; Import-Module ADDSDeployment;";


        //    // DomainMode & ForestMode values are Win2008, Win2008R2, Win2012, Win2012R2, WinThreshold, Default
        //    // WinThreshold is max for OS version (2016&2019 will be 2016 FFL/DFL)
        //    string psCommand = "";


        //    promoteLine = $@"Install-ADDSForest -SafeModeAdministratorPassword $dsrmPassword -InstallDns:$true -Force:$true -NoRebootOnCompletion:$true -DomainName '{ditInfo.ForestName}' -DomainNetbiosName '{ditInfo.NetBIOSDomainName}' -SkipAutoConfigureDns:$false -DatabasePath '{databasePath}' -LogPath '{logsPath}' -SysvolPath '{sysvolPath}' -CreateDnsDelegation:$false -DomainMode '{ditInfo.DomainMode}' -ForestMode '{ditInfo.ForestMode}'";
        //    psCommand = dsrmPasswordLine + featuresLine + promoteLine;

        //    //Log(psCommand, true);

        //    //string toExecute = $@"powershell.exe -Command {psCommand}";
        //    //int processId = ExecuteLocalProcess(toExecute);
        //    Log($"Executing powershell with {psCommand}");
        //    int processId = ExecuteProcess(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", $"-Command {psCommand}");

        //    return processId;
        //}

        static void InstallFeatures()
        {

            string features = "Add-WindowsFeature AD-Domain-Services, RSAT-AD-PowerShell, RSAT-ADDS; Import-Module ADDSDeployment;";

            Log($"Installing ADDS features");
            int processId = ExecuteProcess(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", $"-Command {features}");
            WaitForProcessToComplete(processId);

        }

        static bool AreFeaturesInstalled(bool showMissingFeature)
        {
            //https://stackoverflow.com/questions/26019565/how-can-i-check-if-windows-features-are-activated-using-c-sharp

            List<string> ServerFeaturesRequired = new List<string>();
            List<string> ServerFeaturesInstalled = new List<string>();

            ServerFeaturesRequired.Add("Active Directory Domain Services");
            // ServerFeaturesRequired.Add("DNS Server");
            ServerFeaturesRequired.Add("AD DS Tools");
            // ServerFeaturesRequired.Add("DNS Server Tools");
            ServerFeaturesRequired.Add("AD DS Snap-Ins and Command-Line Tools");
            ServerFeaturesRequired.Add("AD DS and AD LDS Tools");
            ServerFeaturesRequired.Add("Active Directory Administrative Center");
            ServerFeaturesRequired.Add("Active Directory module for Windows PowerShell");



            ManagementClass objMC = new ManagementClass("Win32_ServerFeature");
            ManagementObjectCollection objMOC = objMC.GetInstances();
            foreach (ManagementObject objMO in objMOC)
            {
                string featureName = (string)objMO.Properties["Name"].Value;
                ServerFeaturesInstalled.Add(featureName);
            }


            foreach (string feature in ServerFeaturesRequired)
            {
                if (!ServerFeaturesInstalled.Contains(feature))
                {
                    if (showMissingFeature)
                    {
                        Log($"{feature} not installed");
                    }
                    return false;
                }
            }


            return true;
        }


        static void CheckAndPromote()
        {
            // check if ADDS installed, this should have already happened when we checked for AreFeaturesInstalled()
            ServiceController sc = new ServiceController("NTDS");

            if (sc == null) // not installed, should be installed but disabled
            {
                Log("ADDS is not installed");
                StepFailed("ConsoleStatus:ADDSFailed");
            }


            if (sc.StartType == ServiceStartMode.Disabled) // ADDS installed but not yet promoted, we're good to promote
            {
                Log("NTDS Installed, starting promotion");


                int p = RunDCPROMO();

                WaitForProcessToComplete(p);

                Thread.Sleep(10000);        // to ensure dcpromoui is closed

                int PromotionReturnCode = GetPromotionExitCode();

                switch (PromotionReturnCode)
                {

                    case -1:
                        Log($"Promotion failed");
                        StepFailed("ConsoleStatus:PromoFailed");
                        break;

                    case 1:
                    case 2:
                    case 3:
                    case 4:
                        Log($"Promotion completed ExitCode={PromotionReturnCode}");
                        break;

                    default:
                        Log($"Promotion failed {PromotionReturnCode}");
                        StepFailed("ConsoleStatus:PromoFailed");
                        break;
                }



                {
                    RegistryKey keyToWrite = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\ServerManager\Roles\10");
                    keyToWrite.SetValue("ConfigurationStatus", 2);
                    keyToWrite.Close();
                }


            }


        }

        static int RunDCPROMO()
        {
            string promoteArgs = $@"/unattend /ReplicaOrNewDomain:Domain /NewDomain:Forest /NewDomainDNSName:{ditInfo.DomainName} /DomainNetBiosName:{ditInfo.NetBIOSDomainName} /DomainLevel:{ditInfo.DomainMode} /ForestLevel:{ditInfo.ForestMode} /SafeModeAdminPassword:{settings.dsrmPassword} /DatabasePath:C:\Windows\NTDS /LogPath:C:\Windows\NTDS /SysVolPath:C:\Windows\SYSVOL /AllowDomainReinstall:Yes /CreateDNSDelegation:No /DNSOnNetwork:No /InstallDNS:Yes /RebootOnCompletion:No";

            Log($"Executing dcpromo.exe with {promoteArgs}");

            int processId = ExecuteProcess(@"c:\windows\system32\dcpromo.exe", promoteArgs);
            return processId;
        }

        static void DatabaseOperations()
        {

            CopyDITFiles();
            CopySysVol();
            GetBootKeyOnline();
            GetBootKeyFromIFMRegistry();

            // for some wierd reason setting the boot key using SetBootKey C# code just does not work and causes a BSOD on 2022/25
            // therefore for these two OS versions, will revert to using the PS module which works.
            // even contacted Michael Grafnetter on this issue  and no obvious reason was found !


            if (ditInfo.OSName.Contains("2022") || ditInfo.OSName.Contains("2025"))
            {
                SetBootKeyPS();
            }
            else
            {
                SetBootKey();
            }

            SetLSAPolicy();
            UpdateNTDSRegistry();

        }

        static void CopyDITFiles()
        {
            Log("Copying DIT and log files");

            string quote = "\"";

            string dit = $@"{quote}C:\IFM\Active Directory{quote} {quote}C:\Windows\NTDS{quote} *.dit *.edb *.chk *.jfm *.log *.jrs /MIR /NP /NDL /NJS";

            int pDit = ExecuteProcess(@"C:\Windows\System32\robocopy.exe", dit);
            WaitForProcessToComplete(pDit);

        }

        static void CopySysVol()
        {
            Log("Copying SYSVOL");

            string path = $@"C:\IFM\SYSVOL\{ditInfo.DomainName}   C:\Windows\SYSVOL\Domain /MIR /XD DfsrPrivate /XJ /COPYALL /SECFIX /TIMFIX /NP /NDL";

            int p = ExecuteProcess(@"C:\Windows\System32\robocopy.exe", path);

            WaitForProcessToComplete(p);
        }

        static void SetLSAPolicy()
        {
            try
            {

                LsaDnsDomainInformation newInfo = new LsaDnsDomainInformation()
                {
                    DnsDomainName = ditInfo.DomainName,
                    DnsForestName = ditInfo.ForestName,
                    Guid = ditInfo.DomainGuid.StringAsGuid(),
                    Name = ditInfo.NetBIOSDomainName,
                    Sid = ditInfo.DomainSid.StringAsSid()
                };

                LsaPolicyAccessMask RequiredAccessMask = LsaPolicyAccessMask.ViewLocalInformation | LsaPolicyAccessMask.TrustAdmin;

                LsaPolicy lsaPolicy = new LsaPolicy(RequiredAccessMask);
                lsaPolicy.SetDnsDomainInformation(newInfo);


                Log($"Set LSA Policy DnsForestName={ditInfo.ForestName} DnsDomainName={ditInfo.DomainName} Name={ditInfo.NetBIOSDomainName} Guid={ditInfo.DomainGuid} Sid={ditInfo.DomainSid}");
            }
            catch (Exception err)
            {
                Log($"ERROR: setting LSA Policy {err.Message}");
                StepFailed();
            }
        }

        static void UpdateNTDSRegistry()
        {
            RegistryKey keyToWrite = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\NTDS\Parameters");
            keyToWrite.SetValue("Database restored from backup", 1);

            keyToWrite = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\NTDS\Parameters");
            keyToWrite.SetValue("Configuration NC", restoredForestInfo.configurationNamingContext);

            keyToWrite = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\NTDS\Parameters");
            keyToWrite.SetValue("Root Domain", restoredForestInfo.rootDomainNamingContext);

            DomainControllerInfo domainController = restoredForestInfo.RestoredDomainControllers.Find(x => x.dnsHostName == restoredForestInfo.dnsHostName);
            keyToWrite = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\NTDS\Parameters");
            keyToWrite.SetValue("Machine DN Name", $"{domainController.dsServiceName}");

            keyToWrite = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\NTDS\Parameters");
            keyToWrite.SetValue("Repl Perform Initial Synchronizations", 0);

            keyToWrite = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\NTDS\Parameters");
            keyToWrite.DeleteValue("DSA Database Epoch");


            Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\DNS Server\Zones", false);

            keyToWrite.Close();

            Log($@"Deleted HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\DNS Server\Zones");
            Log($@"Deleted HKLM\System\CurrentControlSet\Services\NTDS\Parameters\DSA Database Epoch");
            Log($@"Updated HKLM\System\CurrentControlSet\Services\NTDS\Parameters\Database restored from backup =1");
            Log($@"Updated HKLM\System\CurrentControlSet\Services\NTDS\Parameters\Configuration NC ={restoredForestInfo.configurationNamingContext}");
            Log($@"Updated HKLM\System\CurrentControlSet\Services\NTDS\Parameters\Machine DN Name ={domainController.dsServiceName}");
            Log($@"Updated HKLM\System\CurrentControlSet\Services\NTDS\Parameters\Root Domain ={restoredForestInfo.rootDomainNamingContext}");
            Log($@"Updated HKLM\System\CurrentControlSet\Services\NTDS\Parameters\Repl Perform Initial Synchronizations =0");

        }

        static void DFSROperations()
        {
            string dfsrSubscriptionDN = $"CN=SYSVOL Subscription,CN=Domain System Volume,CN=DFSR-LocalSettings,{ditInfo.ServerReference}";

            Log($"Fixing DFSR {dfsrSubscriptionDN}");
            try
            {

                LdapConnection ldapConnection = new LdapConnection(localHost);

                ModifyRequest modifyRequest = new ModifyRequest();
                modifyRequest.DistinguishedName = dfsrSubscriptionDN;

                {
                    DirectoryAttributeModification mod = new DirectoryAttributeModification();
                    mod.Name = "msDFSR-RootPath";
                    mod.Add(@"C:\Windows\SYSVOL\domain");
                    mod.Operation = DirectoryAttributeOperation.Replace;
                    modifyRequest.Modifications.Add(mod);
                }


                {
                    DirectoryAttributeModification mod = new DirectoryAttributeModification();
                    mod.Name = "msDFSR-StagingPath";
                    mod.Add($@"C:\Windows\SYSVOL\staging areas\{ditInfo.DomainName}");
                    mod.Operation = DirectoryAttributeOperation.Replace;
                    modifyRequest.Modifications.Add(mod);
                }

                ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);

                Log("DFSR Subscriptions updated");
            }
            catch (Exception err)
            {
                Log($"ERROR: updating DFSR  {err.Message}");
            }

        }

        public static void PollDSNow()
        {
            Log($"Polling AD for DFSR");

            ManagementScope managementScope = new ManagementScope(@"\\.\root\microsoftdfs");

            try
            {
                managementScope.Connect();
            }
            catch (Exception err)
            {
                Log($"ERROR: connecting to WMI {err.Message}");
                return;
            }



            SelectQuery query = new SelectQuery("DfsrConfig");
            ManagementObjectSearcher mgmtSrchr = new ManagementObjectSearcher(managementScope, query);
            ManagementObjectCollection managementObjectCollection = mgmtSrchr.Get();


            foreach (ManagementObject managementObject in managementObjectCollection)
            {

                managementObject.Scope.Options.Authentication = System.Management.AuthenticationLevel.PacketPrivacy;
                managementObject.Scope.Options.Impersonation = ImpersonationLevel.Impersonate;
                managementObject.Scope.Options.EnablePrivileges = true;


                ManagementBaseObject inParams = managementObject.GetMethodParameters("PollDsNow");

                inParams["DcDnsName"] = "localHost";


                ManagementBaseObject outParams = managementObject.InvokeMethod("PollDsNow", inParams, null);

                UInt32 retValue = (UInt32)outParams["ReturnValue"];


                Log($"Return value from PollDSNow {retValue}");


            }
        }

        public static void SetNextStep(string step)
        {
            RegistryKey keyToWrite = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\RestoreFromIFM");
            keyToWrite.SetValue("NextStep", step);
            keyToWrite.Close();
        }

        static string GetNextStep()
        {
            RegistryKey logRegistry = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\RestoreFromIFM");
            if (logRegistry != null)
            {
                try
                {
                    string nextStep = (string)logRegistry.GetValue("NextStep", "");
                    return nextStep;
                }
                catch
                {

                    return "";
                }
            }
            return "";

        }

        static int ExecuteProcess(string process, string arguments, bool quiet = false)
        {
            if (!quiet) Log($"Executing {process} {arguments}");
            Process p = Process.Start(process, arguments);
            Thread.Sleep(1000);
            return p.Id;
        }

        static void ExecuteProcessWithOutput(string process, string arguments, string OutputFile)
        {

            Log($"Executing {process} {arguments}");

            using (System.IO.StreamWriter myFileHandle = new System.IO.StreamWriter($@"C:\RestoreFromIFM\{OutputFile}", true))
            {
                myFileHandle.WriteLine("----------------------------------------------------------------------------------------");
                myFileHandle.WriteLine($"Executing {process} {arguments}");
                myFileHandle.WriteLine();
            }

            Process p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = process,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            p.Start();

            while (!p.StandardOutput.EndOfStream)
            {

                try
                {

                    string line = p.StandardOutput.ReadLine();

                    using (System.IO.StreamWriter myFileHandle = new System.IO.StreamWriter($@"C:\RestoreFromIFM\{OutputFile}", true))
                    {
                        myFileHandle.WriteLine(line);
                    }
                }
                catch
                {

                }
            }


        }

        static void WaitForProcessToComplete(int processId)
        {

            int timeOutMins = 15;

            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            while (stopWatch.Elapsed < TimeSpan.FromMinutes(timeOutMins))
            {
                try
                {
                    // still executing ?
                    Process processByID = Process.GetProcessById(processId);
                }
                catch
                {
                    // could not find the process, so it finished
                    stopWatch.Stop();
                    return;
                }

                Thread.Sleep(1000);
            }

            return;     // false;   // timeout
        }

        static int RenameComputer(string newName)
        {
            // this method does not need the server netbios name

            string currentName = System.Environment.MachineName;

            if (newName == "") return -1;


            if (currentName.ToLower() == newName.ToLower())
            {
                return -2;
            }

            Log($"Renaming {currentName} to {newName}");

            ManagementScope managementScope = new ManagementScope(@"\\.\root\cimv2");

            try
            {
                managementScope.Connect();
            }
            catch (Exception err)
            {
                Log($"ERROR: connecting to WMI for rename {err.Message}");
                return -1;
            }



            SelectQuery query = new SelectQuery("Win32_ComputerSystem");
            ManagementObjectSearcher mgmtSrchr = new ManagementObjectSearcher(managementScope, query);
            ManagementObjectCollection managementObjectCollection = mgmtSrchr.Get();


            foreach (ManagementObject managementObject in managementObjectCollection)
            {

                managementObject.Scope.Options.Authentication = System.Management.AuthenticationLevel.PacketPrivacy;
                managementObject.Scope.Options.Impersonation = ImpersonationLevel.Impersonate;
                managementObject.Scope.Options.EnablePrivileges = true;


                ManagementBaseObject inParams = managementObject.GetMethodParameters("Rename");
                //inParams["UserName"] = _Username;
                //inParams["Password"] = s.localPassword;
                inParams["Name"] = newName;


                ManagementBaseObject outParams = managementObject.InvokeMethod("Rename", inParams, null);

                UInt32 retValue = (UInt32)outParams["ReturnValue"];


                return (int)retValue;

            }

            return -1;
        }

        static void Reboot(RebootOptions rebootOption)
        {
            switch (rebootOption)
            {
                case RebootOptions.Normal:
                    Log("Rebooting");
                    break;

                case RebootOptions.EnableDSRM:
                    Log("Enabling DSRM & Rebooting");
                    EnableDSRM();
                    break;

                case RebootOptions.DisableDSRM:
                    Log("Disabling DSRM & Rebooting");
                    DisableDSRM();
                    break;


            }

            Process.Start(@"C:\Windows\System32\shutdown.exe", "/r /f /t 0");

            Environment.Exit(0);    // stop at this point and let reboot happen
        }

        static void GetDitInfo()
        {

            if (File.Exists(@"C:\RestoreFromIFM\ditInfo.xml"))
            {
                System.Xml.Serialization.XmlSerializer serializer = new System.Xml.Serialization.XmlSerializer(typeof(DitInfo));

                using (System.IO.Stream reader = new System.IO.FileStream(@"C:\RestoreFromIFM\ditInfo.xml", System.IO.FileMode.Open))
                {
                    ditInfo = (DitInfo)serializer.Deserialize(reader);
                }


                return;
            }



            if (!File.Exists(@"C:\IFM\Active Directory\ntds.dit"))
            {
                Log(@"Cannot find C:\IFM\Active Directory\ntds.dit");
                StepFailed();
            }


            try
            {
                Log($"Reading information from DIT");

                DirectoryContext directoryContext;

                directoryContext = new DirectoryContext(@"C:\IFM\Active Directory\ntds.dit", true);

                DomainController domainController = new DomainController(directoryContext);


                ditInfo.dnsHostName = domainController.DNSHostName.ToLower();
                ditInfo.HostName = ditInfo.dnsHostName.Substring(0, ditInfo.dnsHostName.IndexOf('.')).ToLower();
                ditInfo.ForestName = domainController.ForestName.ToLower();
                ditInfo.DomainName = domainController.DomainName.ToLower();
                ditInfo.DomainGuid = domainController.DomainGuid.ToString();
                ditInfo.DomainSid = domainController.DomainSid.ToString();
                ditInfo.ForestMode = ((int)domainController.ForestMode).ToString();
                ditInfo.DomainMode = ((int)domainController.DomainMode).ToString();
                ditInfo.NetBIOSDomainName = domainController.NetBIOSDomainName.ToLower();
                ditInfo.ServerReference = domainController.ServerReference.ToString().ToLower();
                ditInfo.dcObjectGuid = ((Guid)domainController.Guid).ToString();
                ditInfo.ConfigurationNamingContext = domainController.ConfigurationNamingContext.ToString().ToLower();
                //ditInfo.ReplicationEpoch = domainController.ReplicationEpoch;
                ditInfo.DomainNamingContext = domainController.DomainNamingContext.ToString().ToLower();
                ditInfo.DsaGuid = domainController.DsaGuid.ToString();
                ditInfo.InvocationId = domainController.InvocationId.ToString();
                ditInfo.IsGlobalCatalog = domainController.IsGlobalCatalog;
                ditInfo.OSName = domainController.OSName;
                ditInfo.OSVersion = domainController.OSVersion;
                ditInfo.OSVersionMajor = domainController.OSVersionMajor;
                ditInfo.OSVersionMinor = domainController.OSVersionMinor;
                ditInfo.SchemaNamingContext = domainController.SchemaNamingContext.ToString().ToLower(); ;
                ditInfo.SiteName = domainController.SiteName.ToLower();
                ditInfo.BackupExpiration = (DateTime)domainController.BackupExpiration;

                if (domainController.WritablePartitions.Count() == 0)
                {
                    ditInfo.isRODC = true;
                }
                else
                {
                    ditInfo.isRODC = false;
                }

                domainController.Dispose();
                directoryContext.Dispose();

                System.Xml.Serialization.XmlSerializer x = new System.Xml.Serialization.XmlSerializer(ditInfo.GetType());
                System.IO.StreamWriter myFile = new System.IO.StreamWriter(@"C:\RestoreFromIFM\ditInfo.xml", false);
                x.Serialize(myFile, ditInfo);
                myFile.Close();


            }
            catch (Exception err)
            {
                Log($"ERROR: getting DIT Info {err.Message}");
                StepFailed();
            }
        }

        static void ShowDITInfo()
        {
            Log($"Local IP address is          {localIPAddress}");

            Log($"------------------ DIT Infomation ------------------------");
            Log($"DNSHostName                  {ditInfo.dnsHostName}");
            Log($"HostName                     {ditInfo.HostName}");
            Log($"ForestName                   {ditInfo.ForestName}");
            Log($"DomainName                   {ditInfo.DomainName}");

            Log($"DomainNamingContext          {ditInfo.DomainNamingContext}");
            Log($"ConfigurationNamingContext   {ditInfo.ConfigurationNamingContext}");
            Log($"SchemaNamingContext          {ditInfo.SchemaNamingContext}");

            Log($"DomainGuid                   {ditInfo.DomainGuid}");
            Log($"DomainSid                    {ditInfo.DomainSid}");
            Log($"ForestMode                   {ditInfo.ForestMode}");
            Log($"DomainMode                   {ditInfo.DomainMode}");
            Log($"ServerReference              {ditInfo.ServerReference}");
            //Log($"ReplicationEpoch             {ditInfo.ReplicationEpoch}");


            Log($"DsaGuid                      {ditInfo.DsaGuid}");
            Log($"InvocationId                 {ditInfo.InvocationId}");
            Log($"IsGlobalCatalog              {ditInfo.IsGlobalCatalog}");
            Log($"OSName                       {ditInfo.OSName}");
            Log($"OSVersion                    {ditInfo.OSVersion}");
            Log($"OSVersionMajor               {ditInfo.OSVersionMajor}");
            Log($"OSVersionMinor               {ditInfo.OSVersionMinor}");
            Log($"IFM Expiration               {ditInfo.BackupExpiration}");


        }

        static void ShowRestoredForestInfo()
        {
            Log($"------------------ AD Infomation (RootDSE) --------------");
            Log($"dnsHostName                  {restoredForestInfo.dnsHostName}");
            Log($"domainDNS                    {restoredForestInfo.domainDNS}");
            Log($"netbiosName                  {restoredForestInfo.netbiosName}");
            Log($"rootDomainNamingContext      {restoredForestInfo.rootDomainNamingContext}");
            Log($"rootDomainDNS                {restoredForestInfo.rootDomainDNS}");
            Log($"defaultNamingContext         {restoredForestInfo.defaultNamingContext}");
            Log($"configurationNamingContext   {restoredForestInfo.configurationNamingContext}");
            Log($"schemaNamingContext          {restoredForestInfo.schemaNamingContext}");
            foreach (string namingContext in restoredForestInfo.namingContexts)
            {
                Log($"namingContext                {namingContext}");
            }
            Log($"isSingleDomainForest         {restoredForestInfo.isSingleDomainForest}");
            Log($"isRootDomainController       {restoredForestInfo.isRootDomainController}");
            Log($"IFM taken at                 {ditInfo.BackupExpiration.AddDays(-restoredForestInfo.tsl)}");


            Log($"------------------ Domain Controller Information -------");
            foreach (DomainControllerInfo domainControllerInfo in restoredForestInfo.AllDomainControllers)
            {

                Log($"distinguishedName            {domainControllerInfo.distinguishedName}");
                Log($"dsServiceName                {domainControllerInfo.dsServiceName}");
                Log($"dnsHostName                  {domainControllerInfo.dnsHostName}");
                Log($"domainDNS                    {domainControllerInfo.domainDNS}");
                Log($"domainOnly                   {domainControllerInfo.domainOnly}");
                Log($"invocationId                 {domainControllerInfo.invocationId}");
                Log($"dsaGuid(objectGuid)          {domainControllerInfo.dsaGuid}");
                Log($"serverReference              {domainControllerInfo.serverReference}");
                Log($"site                         {domainControllerInfo.site}");
                Log($"isGC                         {domainControllerInfo.isGC}");
                Log($"restored                     {domainControllerInfo.restored}");
                if (domainControllerInfo.restored)
                {
                    Log($"old IP                       {domainControllerInfo.oldIP}");
                    Log($"new IP                       {domainControllerInfo.newIP}");
                }

                Log("");
            }

            Log($"------------------ FSMO Infomation ------------------------");
            Log($"fsmo Schema                  {restoredForestInfo.fsmoSchema}");
            Log($"fsmo Naming                  {restoredForestInfo.fsmoNaming}");
            Log($"fsmo PDC                     {restoredForestInfo.fsmoPDC}");
            Log($"fsmo RID                     {restoredForestInfo.fsmoRID}");
            Log($"fsmo Infrastructure          {restoredForestInfo.fsmoInfra}");


            Log($"------------------ Domain Infomation ------------------------");
            foreach (DomainInfo d in restoredForestInfo.domainInfo)
            {
                Log("");
                Log($"domain context               {d.domainContext}");
                Log($"domainDNS                    {d.domainDNS}");
                //Log($"domainguid                   {d.domainGuid}");
            }


        }

        static void ShowDomainControllersToBeRestoredRemoved()
        {
            Log($"------------------ Domain Controllers To Be Restored/Removed --------------------");
            foreach (DomainControllerInfo domainController in restoredForestInfo.RestoredDomainControllers)
            {
                Log($"{domainController.dnsHostName} will be restored  {domainController.oldIP}-->{domainController.newIP}");
            }

            foreach (DomainControllerInfo domainController in restoredForestInfo.NotRestoredDomainControllers)
            {
                Log($"{domainController.dnsHostName} will be removed in recovered forest");
            }


            Log($"Schema Master will be {restoredForestInfo.SchemaMaster.dnsHostName} Seized={restoredForestInfo.seizeSchemaMaster}");
            Log($"Naming Master will be {restoredForestInfo.NamingMaster.dnsHostName} Seized={restoredForestInfo.seizeNamingMaster}");
            Log($"PDC Master will be    {restoredForestInfo.PDCMaster.dnsHostName} Seized={restoredForestInfo.seizePDCMaster}");
            Log($"RID Master will be    {restoredForestInfo.RIDMaster.dnsHostName} Seized={restoredForestInfo.seizeRIDMaster}");
            Log($"Infra Master will be  {restoredForestInfo.InfraMaster.dnsHostName} Seized={restoredForestInfo.seizeInfraMaster}");

            Log($"------------------ Starting Restore Process ------------------------");

        }

        static void GetBootKeyFromIFMRegistry()
        {
            // will get the boot key from the IFM
            // needs DSInternals.DataStore;

            try
            {

                IFMBootKey = BootKeyRetriever.GetBootKey(@"c:\ifm\registry\SYSTEM");

                string s = "";
                foreach (byte b in IFMBootKey)
                {
                    s += b.ToString("x2");
                }

                Log($"IFM bootkey is {s}");
            }
            catch (Exception err)
            {
                Log($"ERROR: getting bootkey from IFM {err.Message}");
                StepFailed();
            }

        }

        static void GetBootKeyOnline()
        {
            try
            {
                OnlineBootKey = BootKeyRetriever.GetBootKey();

                string s = "";
                foreach (byte b in OnlineBootKey)
                {
                    s += b.ToString("x2");
                }

                Log($"Online bootkey is {s}");
            }
            catch (Exception err)
            {
                Log($"ERROR: getting online bootkey {err.Message}");
                StepFailed();
            }
        }

        static void SetBootKey()
        {
            // change the bootkey in the IFM with the online bootkey
            try
            {
                DirectoryContext directoryContext;
                directoryContext = new DirectoryContext(@"C:\Windows\NTDS\ntds.dit", false, @"C:\Windows\NTDS");
                DirectoryAgent directoryAgent = new DirectoryAgent(directoryContext);
                directoryAgent.ChangeBootKey(IFMBootKey, OnlineBootKey);
                directoryAgent.Dispose();
                directoryContext.Dispose();
                directoryAgent = null;
                directoryContext = null;


                Log("Bootkey in IFM replaced with Online bootkey");
            }
            catch (Exception err)
            {
                Log($"ERROR: setting bootkey in IFM {err.Message}");
                StepFailed();
            }
        }

        static void SetBootKeyPS()
        {
            // needed for 2022/2025 as C# code does not work (for me!)

            try
            {
                string oldKey = "";
                string newKey = "";

                foreach (byte b in OnlineBootKey)
                {
                    newKey += b.ToString("x2");
                }

                foreach (byte b in IFMBootKey)
                {
                    oldKey += b.ToString("x2");
                }

                string psCommand = $@"Set-ADDBBootKey -DatabasePath 'C:\Windows\NTDS\ntds.dit' -LogPath 'C:\Windows\NTDS' -OldBootKey '{oldKey}' -NewBootKey '{newKey}' -Force";
                int processId = ExecuteProcess(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", $"-Command {psCommand}", true);
                WaitForProcessToComplete(processId);


                Log("Bootkey in IFM replaced with Online bootkey (PS)");

            }
            catch (Exception err)
            {
                Log($"ERROR: setting bootkey in IFM {err.Message}");
                StepFailed();
            }
        }

        static void EnableDSRM()
        {
            Process.Start(@"c:\windows\system32\bcdedit.exe", "/set safeboot dsrepair");
            Thread.Sleep(5000);
        }

        static void DisableDSRM()
        {
            Process.Start(@"c:\windows\system32\bcdedit.exe", "/deletevalue safeboot");
            Thread.Sleep(5000);
        }

        static void Log(string s, bool logOnly = false)
        {

            string toLog = s.Replace("\r\n", "");

            using (System.IO.StreamWriter serverFile = new System.IO.StreamWriter(@"C:\RestoreFromIFM\RestoreFromIFM.log", true))
            {
                serverFile.WriteLine(DateTime.UtcNow.ToString() + " " + toLog);
            }

            if (logOnly) return;


            SendToConsole(DateTime.UtcNow.ToString() + " " + toLog);

        }

        static void SendToConsole(string s)
        {
            UdpClient udpClient = new UdpClient();

            Byte[] sendBytes = Encoding.ASCII.GetBytes(s);
            try
            {
                udpClient.Send(sendBytes, sendBytes.Length, settings.remoteIP, settings.udpPort);
                Thread.Sleep(100);      // wait 100ms so that traffic is sent to RIFM Monitor, this ensures that the line sent will appear in order it was sent
            }
            catch
            {

            }
        }

        static void SendToConsole(string s, string remoteIP)
        {
            UdpClient udpClient = new UdpClient();

            Byte[] sendBytes = Encoding.ASCII.GetBytes(s);
            try
            {
                udpClient.Send(sendBytes, sendBytes.Length, remoteIP, settings.udpPort);
            }
            catch
            {

            }
        }

        public static void MonitorFor1704Event()
        {
            EventLogSession session = new EventLogSession();


            string queryString = "*[System/EventID=1704 or System/EventID=6006]";       /// 6006 is when a timeout to read gpo happens, Waiting to apply settings has been done

            EventLogQuery query = new EventLogQuery("Application", PathType.LogName, queryString)
            {
                TolerateQueryErrors = true,
                Session = session
            };

            EventLogWatcher logWatcher = new EventLogWatcher(query);

            logWatcher.EventRecordWritten += new EventHandler<EventRecordWrittenEventArgs>(LogWatcher_EventRecordWritten);

            try
            {
                logWatcher.Enabled = true;
            }
            catch (EventLogException err)
            {
                Log($"ERROR: starting log watcher {err.Message}");
                StepFailed();
            }

            Log("Waiting for directory services to fully initialise (will take several minutes)");

            NTDSAvailable = false;

            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            while (!NTDSAvailable)
            {
                Thread.Sleep(1000);

                // every 30 secs show update on time elapsed
                if (stopWatch.Elapsed.Seconds % 30 == 0)
                {
                    Log($"Waiting for directory services to fully initialise " + stopWatch.Elapsed.Minutes.ToString("D2") + ":" + stopWatch.Elapsed.Seconds.ToString("D2") + " elapsed");

                    FlushDNSServerCache(true);
                    FlushDNSResolverCache(true);


                }


            }

            stopWatch.Stop();

            NTDSAvailable = false;

            logWatcher.EventRecordWritten -= new EventHandler<EventRecordWrittenEventArgs>(LogWatcher_EventRecordWritten);



        }

        public static void LogWatcher_EventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            DateTime? TimeCreated = e.EventRecord.TimeCreated;
            int Id = e.EventRecord.Id;                              // the actual event id, 5136 etc
            long? RecordId = e.EventRecord.RecordId;                 // the unique event record number

            NTDSAvailable = true;
        }

        static void MetaDataCleanup()
        {


            LdapConnection ldapConnection = new LdapConnection(localHost);
            SearchRequest searchRequest = null;
            SearchResponse searchResponse = null;




            //#region Remove non restored ntdsConnections
            //{
            //    // for the servers that were removed, delete any ntdsConnection that references any server that was not restored
            //    Log($"Deleting ntdsConnections for removed domain controllers");

            //    searchRequest = new SearchRequest($"CN=Sites,{ditInfo.ConfigurationNamingContext}", $"(objectClass=ntdsConnection)", SearchScope.Subtree, null);
            //    searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);


            //    foreach (SearchResultEntry entry in searchResponse.Entries)
            //    {
            //        string fromServer = ((string)entry.Attributes["fromServer"][0]).ToLower();

            //        foreach (DomainControllerInfo domainController in restoredForestInfo.AllDomainControllers)
            //        {
            //            if (domainController.restored) continue;

            //            if (fromServer.StartsWith($"cn=ntds settings,cn={domainController.hostNameOnly},"))
            //            {
            //                // delete this connection object

            //                try
            //                {
            //                    DeleteRequest deleteRequest = new DeleteRequest();
            //                    deleteRequest.DistinguishedName = entry.DistinguishedName;
            //                    deleteRequest.Controls.Add(new TreeDeleteControl());
            //                    DeleteResponse deleteResponse = (DeleteResponse)ldapConnection.SendRequest(deleteRequest);
            //                    Log($"Deleted ntdsConnection {entry.DistinguishedName}");
            //                }
            //                catch (Exception err)
            //                {
            //                    Log($"ERROR: deleting ntdsConnection {entry.DistinguishedName} {err.Message}");
            //                }

            //            }

            //        }
            //    }
            //}

            #region Remove all ntdsConnections
            {

                Log($"Deleting all ntdsConnections");

                searchRequest = new SearchRequest($"CN=Sites,{ditInfo.ConfigurationNamingContext}", $"(objectClass=ntdsConnection)", SearchScope.Subtree, null);
                searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);


                foreach (SearchResultEntry entry in searchResponse.Entries)
                {

                    try
                    {
                        DeleteRequest deleteRequest = new DeleteRequest();
                        deleteRequest.DistinguishedName = entry.DistinguishedName;
                        deleteRequest.Controls.Add(new TreeDeleteControl());
                        DeleteResponse deleteResponse = (DeleteResponse)ldapConnection.SendRequest(deleteRequest);
                        Log($"Deleted ntdsConnection {entry.DistinguishedName}");
                    }
                    catch (Exception err)
                    {
                        Log($"ERROR: deleting ntdsConnection {entry.DistinguishedName} {err.Message}");
                    }
                }
            }
            #endregion

            // now delete the actual server object of servers that were not restored
            Log($"Deleting removed server objects");
            foreach (DomainControllerInfo domainController in restoredForestInfo.NotRestoredDomainControllers)
            {

                searchRequest = new SearchRequest($"CN=Sites,{ditInfo.ConfigurationNamingContext}", $"(&(objectClass=server)(dnsHostname={domainController.dnsHostName}))", SearchScope.Subtree, null);
                searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);

                foreach (SearchResultEntry entry in searchResponse.Entries)
                {
                    try
                    {
                        DeleteRequest deleteRequest = new DeleteRequest();
                        deleteRequest.DistinguishedName = entry.DistinguishedName;
                        deleteRequest.Controls.Add(new TreeDeleteControl());
                        DeleteResponse deleteResponse = (DeleteResponse)ldapConnection.SendRequest(deleteRequest);
                        Log($"Deleted server  {entry.DistinguishedName}");
                    }
                    catch (Exception err)
                    {
                        Log($"ERROR: Deleting server  {entry.DistinguishedName} {err.Message}");
                    }

                }

            }



            // now delete the computer object in domain controllers ou for domain controllers that were not restored for this domain
            foreach (DomainControllerInfo domainController in restoredForestInfo.NotRestoredDomainControllers)
            {

                if (domainController.domainDNS != restoredForestInfo.domainDNS) continue;       // only want domain controllers of this domain

                // delete the server and leaf objects
                try
                {
                    DeleteRequest deleteRequest = new DeleteRequest(domainController.serverReference);
                    deleteRequest.Controls.Add(new TreeDeleteControl());
                    DeleteResponse deleteResponse = (DeleteResponse)ldapConnection.SendRequest(deleteRequest);
                    Log($"Deleted computer object {domainController.serverReference}");
                }
                catch (Exception err)
                {
                    Log($"ERROR: Deleting {domainController.serverReference} {err.Message}");
                    StepFailed();
                }
            }

        }

        static void FixDNS()
        {






            // this is a list of domain controllers IPs that have been restored
            List<string> ipWhiteList = new List<string>();

            foreach (DomainControllerInfo domainController in restoredForestInfo.RestoredDomainControllers)
            {
                ipWhiteList.Add(domainController.newIP);
            }



            #region _msdcs.<root domain>

            Log($"********** Processing zone _msdcs.{restoredForestInfo.rootDomainDNS} **********");

            // remove All NS records for dc's on _msdcs.<root domain> this is stored on the @ object in ForestDNSZones

            Log($"Deleting NS records on _msdcs.{restoredForestInfo.rootDomainDNS}");

            RemoveAllNSEntries($"dc=@,dc=_msdcs.{restoredForestInfo.rootDomainDNS},cn=microsoftdns,dc=forestdnszones,{restoredForestInfo.rootDomainNamingContext}");

            // delete any glue records
            Log($"Deleting glue records on _msdcs.{restoredForestInfo.rootDomainDNS}");
            foreach (DomainControllerInfo domainController in restoredForestInfo.AllDomainControllers)
            {
                // delete the glue record also, note this has a trailing . after name
                // not all will exist
                DeleteDNSObject($"dc={domainController.dnsHostName}.,dc=_msdcs.{restoredForestInfo.rootDomainDNS},cn=microsoftdns,dc=forestdnszones,{restoredForestInfo.rootDomainNamingContext}", false);
            }

            // Need to reload the zone after any hard delete in AD, this way the deleted records are still not in DNS cache
            // we need to do this step here as after we have deleted all the old glue records, we add them back with new ip address
            // if we this is not done, then glue records could have both old and new ip addresses 
            ReloadZone($"_msdcs.{restoredForestInfo.rootDomainDNS}");

            // Add all restored servers as NS for _msdcs.<root dns> and also their glue records


            foreach (DomainControllerInfo domainController in restoredForestInfo.RestoredDomainControllers)
            {
                AddNSRecord($"_msdcs.{restoredForestInfo.rootDomainDNS}", domainController.dnsHostName);

                AddHostRecord($"_msdcs.{restoredForestInfo.rootDomainDNS}", $"{domainController.dnsHostName}.", domainController.newIP);
            }



            #region DSA Guids
            {
                // remove all dsaGuid's that were not restored
                // i.e. <dsaGuid> CNAME dc00.root.local
                Log($"Deleting DSA Guids for domain controllers that were not restored");
                foreach (DomainControllerInfo domainController in restoredForestInfo.NotRestoredDomainControllers)
                {
                    string dn = $"dc={domainController.dsaGuid},dc=_msdcs.{restoredForestInfo.rootDomainDNS},cn=microsoftdns,dc=forestdnszones,{restoredForestInfo.rootDomainNamingContext}";
                    DeleteDNSObject(dn);
                }
            }
            #endregion

            // remove any _ldap, _kerberos, _kpasswd, _gc entries for domain controllers that were not restored
            Log($"Deleting _ldap, _kerberos, _gc and _kpasswd SRV records for domain controllers that were not restored");
            foreach (DomainControllerInfo domainController in restoredForestInfo.NotRestoredDomainControllers)
            {
                RemoveSRVRecord(domainController.dnsHostName, $"DC=_msdcs.{restoredForestInfo.rootDomainDNS},CN=MicrosoftDNS,DC=ForestDnsZones,{restoredForestInfo.rootDomainNamingContext}");
            }


            // remove _kerberos for any restored domain controller unless RID master (as kdc will only be running on this)
            //Log($"Deleting _kerberos SRV records for domain controllers that were restored, except RID masters");
            //foreach (DomainControllerInfo domainController in restoredForestInfo.RestoredDomainControllers)
            //{
            //    if (domainController.dnsHostName != restoredForestInfo.RIDMaster.dnsHostName)
            //    {
            //        RemoveSRVRecord(domainController.dnsHostName, $"DC=_msdcs.{restoredForestInfo.rootDomainDNS},CN=MicrosoftDNS,DC=ForestDnsZones,{restoredForestInfo.rootDomainNamingContext}", "(name=_kerberos*)");
            //    }
            //}





            // fix the A records on gc._msdcs.<root domain>
            // change IP address for domain controller restored
            Log($"Updating IP addresses on DC=gc,DC=_msdcs.{restoredForestInfo.rootDomainDNS},CN=MicrosoftDNS,DC=ForestDnsZones,{restoredForestInfo.rootDomainNamingContext}");
            foreach (DomainControllerInfo domainController in restoredForestInfo.RestoredDomainControllers)
            {
                UpdateHostRecord($"DC=gc,DC=_msdcs.{restoredForestInfo.rootDomainDNS},CN=MicrosoftDNS,DC=ForestDnsZones,{restoredForestInfo.rootDomainNamingContext}", domainController.oldIP, domainController.newIP);
            }

            // fix the A records on gc._msdcs.<root domain>
            // for the domain controllers not restored, we don't know their old ip
            // so we'll remove anything that not been restored
            // remove A records for non restored servers
            // step must be done after changing old to new IP addresses above !
            Log($"Removing A records for domain controller that were not restored on DC=gc,DC=_msdcs.{restoredForestInfo.rootDomainDNS},CN=MicrosoftDNS,DC=ForestDnsZones,{restoredForestInfo.rootDomainNamingContext}");
            RemoveHostRecord($"DC=gc,DC=_msdcs.{restoredForestInfo.rootDomainDNS},CN=MicrosoftDNS,DC=ForestDnsZones,{restoredForestInfo.rootDomainNamingContext}", ipWhiteList);


            // only done on root domain controllers
            if (restoredForestInfo.isRootDomainController)
            {

                Log($"Updating host records on DC=ForestDnsZones,dc={restoredForestInfo.rootDomainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.rootDomainNamingContext}");


                // this domain controllers ip address will be dynamically added, it means we'll end up with two ip addresses of this domain controller
                // so delete the dynmically added one
                RemoveHostRecord($"DC=ForestDnsZones,dc={restoredForestInfo.rootDomainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.rootDomainNamingContext}", localIPAddress);



                foreach (DomainControllerInfo domainController in restoredForestInfo.RestoredDomainControllers)
                {
                    UpdateHostRecord($"DC=ForestDnsZones,dc={restoredForestInfo.rootDomainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.rootDomainNamingContext}", domainController.oldIP, domainController.newIP);
                }

                // remove the ones which were not restored
                Log($"Removing host records for domain controllers that were not restored on DC=ForestDnsZones,DC={restoredForestInfo.rootDomainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.rootDomainNamingContext}");
                RemoveHostRecord($"DC=ForestDnsZones,DC={restoredForestInfo.rootDomainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.rootDomainNamingContext}", ipWhiteList);
            }



            #endregion _msdcs.<root domain>





            #region domainDNS

            Log($"********** Processing zone {restoredForestInfo.domainDNS} **********");

            // remove NS records for dc's that were not restored on this zone

            Log($"Deleting NS for domain controllers that were not restored");

            foreach (DomainControllerInfo domainController in restoredForestInfo.NotRestoredDomainControllers)
            {
                string dn = $"dc=@,DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}";
                RemoveNSEntry(dn, domainController.dnsHostName);
            }



            // this domain controllers ip address will be dynamically added, it means we'll end up with two ip addresses of this domain controller
            // so delete the dynmically added one
            RemoveHostRecord($"dc=@,DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}", localIPAddress);

            // fix the A records on @ for domainDNS to their new ip address
            // change IP address for domain controller restored
            Log($"Updating glue records for domain controllers that were restored");
            foreach (DomainControllerInfo domainController in restoredForestInfo.RestoredDomainControllers)
            {
                UpdateHostRecord($"dc=@,DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}", domainController.oldIP, domainController.newIP);
            }



            // fix the A records on @ for domainDNS
            // for the domain controllers not restored, we don't know their old ip
            // so we'll remove anything that not been restored
            Log($"Removing glue records for domain controllers that were not restored");
            RemoveHostRecord($"dc=@,DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}", ipWhiteList);



            // delete the A records for domain controllers not restored
            Log($"Removing host records of domain controllers that were not restored");
            foreach (DomainControllerInfo domainController in restoredForestInfo.NotRestoredDomainControllers)
            {
                DeleteDNSObject($"dc={domainController.hostNameOnly},DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}", false);
            }


            // update A records for domain controllers to new IP address
            Log($"Updating IP addresses of domain controllers that were restored");
            foreach (DomainControllerInfo domainController in restoredForestInfo.RestoredDomainControllers)
            {
                UpdateHostRecord($"dc={domainController.hostNameOnly},DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}", domainController.oldIP, domainController.newIP);
            }



            // remove any _ldap, _kerberos, _kpasswd, _gc entries for domain controllers that were not restored
            Log($"Deleting _ldap, _kerberos, _gc and _kpasswd SRV records for domain controllers that were not restored");
            foreach (DomainControllerInfo domainController in restoredForestInfo.NotRestoredDomainControllers)
            {
                RemoveSRVRecord(domainController.dnsHostName, $"DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}");
            }




            Log($"Updating host records of restored domain controllers on DC=DomainDnsZones,DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}");

            // this domain controllers ip address will be dynamically added, it means we'll end up with two ip addresses of this domain controller
            // so delete the dynmically added one
            RemoveHostRecord($"DC=DomainDnsZones,DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}", localIPAddress);


            foreach (DomainControllerInfo domainController in restoredForestInfo.RestoredDomainControllers)
            {
                UpdateHostRecord($"DC=DomainDnsZones,DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}", domainController.oldIP, domainController.newIP);
            }




            // remove the ones which were not restored
            Log($"Removing host records of restored domain controllers that were not restored on DC=DomainDnsZones,DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}");
            RemoveHostRecord($"DC=DomainDnsZones,DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}", ipWhiteList);



            // fix delegation for _msdcs ONLY done in root domain 

            if (restoredForestInfo.isRootDomainController)
            {
                Log($"Fixing delegations for _msdcs,DC={restoredForestInfo.domainDNS}");

                // we'll remove all the entries first and then add all restored domain controllers
                // as we added all domain controllers as NS for _msdc.<root domain> in _msdcs processing

                RemoveAllNSEntries($"DC=_msdcs,DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}");

                Thread.Sleep(2000);

                foreach (DomainControllerInfo domainController in restoredForestInfo.RestoredDomainControllers)
                {
                    AddDelegation("_msdcs", restoredForestInfo.rootDomainDNS, domainController.dnsHostName);
                }

            }





            #region delegations
            // do we have any domain delegations that need to be fixed

            Log($"Fixing delegations for {restoredForestInfo.domainDNS}");

            foreach (DomainInfo d in restoredForestInfo.domainInfo)
            {

                if (DelegationExists(restoredForestInfo.domainDNS, d.domainOnly))
                {
                    Log($"Delegation of {d.domainOnly} exists in {restoredForestInfo.domainDNS}");

                    {
                        // Delete all NS records for the delegation
                        Log($"Deleting name servers for delegation in zone {restoredForestInfo.domainDNS} for {d.domainOnly}");
                        RemoveAllNSEntries($"DC={d.domainOnly},DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}");
                    }

                    {
                        // Delete any glue records - we'll delete for each domain controller in the original forest even though they may not have
                        // been used in the delegation

                        foreach (DomainControllerInfo domainController in restoredForestInfo.AllDomainControllers)
                        {
                            if (domainController.domainDNS != d.domainDNS) continue;

                            Log($"Deleting glue record {domainController.dnsHostName} A {domainController.oldIP}");
                            DeleteDNSObject($"dc={domainController.hostNameOnly}.{d.domainOnly},DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}", false);
                        }


                    }

                    ReloadZone(restoredForestInfo.domainDNS);


                    {
                        // now add NS and glue records back
                        Log($"Adding name servers for delegation in zone {restoredForestInfo.domainDNS} for {d.domainOnly}");

                        foreach (DomainControllerInfo domainController in restoredForestInfo.RestoredDomainControllers)
                        {
                            // add delegation for each domain controller regardless if it had one before or not
                            if (domainController.domainDNS == d.domainDNS)
                            {
                                AddDelegation($"{d.domainOnly}", restoredForestInfo.domainDNS, $"{domainController.dnsHostName}");
                            }
                        }

                        Log($"Adding glue record for delegation in zone {restoredForestInfo.domainDNS} for {d.domainOnly}");

                        foreach (DomainControllerInfo domainController in restoredForestInfo.RestoredDomainControllers)
                        {
                            // add delegation for each domain controller regardless if it had one before or not
                            if (domainController.domainDNS == d.domainDNS)
                            {
                                AddHostRecord(restoredForestInfo.domainDNS, $"{domainController.hostNameOnly}.{d.domainOnly}", domainController.newIP);
                            }
                        }
                    }


                }

            }
            #endregion delegations




            #endregion domainDNS

            #region Change Kerberos Priority
            //ChangeKerberosWeight(restoredForestInfo.RIDMaster.dnsHostName, $"DC=_msdcs.{restoredForestInfo.rootDomainDNS},CN=MicrosoftDNS,DC=ForestDnsZones,{restoredForestInfo.rootDomainNamingContext}",200);
            //ChangeKerberosWeight(restoredForestInfo.RIDMaster.dnsHostName, $"DC={restoredForestInfo.domainDNS},CN=MicrosoftDNS,DC=DomainDnsZones,{restoredForestInfo.defaultNamingContext}",200);
            #endregion

            #region Fix AD Conditional Forwarders
            FixADConditionalForwarders($"cn=microsoftdns,dc=forestdnszones,{restoredForestInfo.rootDomainNamingContext}");      // replicated in forest
            FixADConditionalForwarders($"cn=microsoftdns,dc=domaindnszones,{restoredForestInfo.defaultNamingContext}");         // replicated in domain
            #endregion


        }

        #region DNS Helpers

        static void ConfigureDNSSettings()
        {

            // find the first domain controller of the root domain that is being restored
            // we'll use this as the primary DNS address for all domain controllers and also set this as the conditional forwarder
            // on all other restored domain controller except the one with this IP

            DomainControllerInfo rootDomainController = restoredForestInfo.RestoredDomainControllers.Find(x => x.domainDNS == restoredForestInfo.rootDomainDNS);

            string rootIP = rootDomainController.newIP;

            // Set the DNS server on tcp/ip  & forwarders
            if (localIPAddress != rootIP)
            {
                Log($"Setting DNS server to {localIPAddress}");
                SetDNSServers(localIPAddress);

                Log($"Setting DNS forwarder to {rootIP}");
                SetConditionalForwarder(rootIP);
            }
        }

        static void RemoveAllNSEntries(string distinguishedName)
        {
            SearchResponse searchResponse;

            LdapConnection ldapConnection = new LdapConnection(localHost);


            try
            {
                SearchRequest searchRequest = new SearchRequest(distinguishedName, "(objectClass=*)", SearchScope.Base, null);
                searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);
            }
            catch (Exception err)
            {
                Log($"ERROR: Cannot find {distinguishedName} {err.Message}");
                return;
            }


            byte[][] dnsRecord = (byte[][])searchResponse.Entries[0].Attributes["dnsRecord"].GetValues(typeof(byte[]));

            string fqdn = null;

            foreach (byte[] byteArray in dnsRecord)
            {

                if (byteArray[2] == 2)        // Delegation
                {
                    fqdn = DecodeFQDN(byteArray, 24);

                    try
                    {

                        ModifyRequest modifyRequest = new ModifyRequest(distinguishedName);

                        DirectoryAttributeModification mod = new DirectoryAttributeModification();
                        mod.Name = "dnsRecord";
                        mod.Operation = DirectoryAttributeOperation.Delete;
                        mod.Add(byteArray);
                        modifyRequest.Modifications.Add(mod);

                        ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);


                        Log($"Removed NS delegation for {fqdn} from {distinguishedName}");


                    }
                    catch (Exception err)
                    {
                        Log($"ERROR: Removing NS delegation {fqdn} from {distinguishedName} {err.Message}");
                    }
                }
            }
            ldapConnection.Dispose();


        }

        static void RemoveNSEntry(string distinguishedName, string dnsHostName)
        {
            SearchResponse searchResponse;

            LdapConnection ldapConnection = new LdapConnection(localHost);


            try
            {
                SearchRequest searchRequest = new SearchRequest(distinguishedName, "(objectClass=*)", SearchScope.Base, null);
                searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);
            }
            catch (Exception err)
            {
                Log($"ERROR: Cannot find {distinguishedName} {err.Message}");
                return;
            }


            byte[][] dnsRecord = (byte[][])searchResponse.Entries[0].Attributes["dnsRecord"].GetValues(typeof(byte[]));

            byte[] deleteDnsRecord = null;


            foreach (byte[] byteArray in dnsRecord)
            {

                if (byteArray[2] == 2)        // Delegation
                {
                    string fqdn = DecodeFQDN(byteArray, 24);

                    if (fqdn.ToLower() == dnsHostName.ToLower())
                    {
                        deleteDnsRecord = byteArray;
                    }
                }
            }

            if (deleteDnsRecord != null)
            {

                try
                {

                    ModifyRequest modifyRequest = new ModifyRequest(distinguishedName);

                    DirectoryAttributeModification mod = new DirectoryAttributeModification();
                    mod.Name = "dnsRecord";
                    mod.Operation = DirectoryAttributeOperation.Delete;
                    mod.Add(deleteDnsRecord);
                    modifyRequest.Modifications.Add(mod);

                    ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);


                    Log($"Removed NS delegation {dnsHostName} from {distinguishedName}");


                }
                catch (Exception err)
                {
                    Log($"ERROR: Removing NS delegation {dnsHostName} from {distinguishedName} {err.Message}");

                    ldapConnection.Dispose();
                    return;
                }
            }

            ldapConnection.Dispose();

            return;


        }

        static string DecodeFQDN(byte[] byteArray, int startIndex)
        {


            byte fqdnSize = byteArray[startIndex];                // including terminating 0 and 1 byte for fqdnLabels
            byte fqdnLabels = byteArray[startIndex + 1];          // number of labels in fqdn

            string fqdn = "";

            // [label1Chars]label1[label2Chars[label2][label3Chars][label3][00]
            // e.g. myhost.widget.acme.com
            // fqdnsize=24  size of myhost.widget.acme.com + terminating 0
            // fqdnlabels =4    myhost, acme, widget, com

            int i = startIndex + 2;
            while (true)
            {
                int labelSize = byteArray[i];
                if (labelSize == 0) break;
                byte[] buffer = byteArray.Skip(i + 1).Take(labelSize).ToArray();                  // add 1 for labelSize
                string label = System.Text.Encoding.UTF8.GetString(buffer, 0, buffer.Length);
                fqdn += label + ".";
                i = i + buffer.Length + 1;
            }


            fqdn = fqdn.Substring(0, fqdn.Length - 1);          //remove last .

            return fqdn;
        }

        static void DeleteDNSObject(string distinguishedName, bool showError = true)
        {
            LdapConnection ldapConnection = new LdapConnection(localHost);

            try
            {
                DeleteRequest deleteRequest = new DeleteRequest(distinguishedName);
                DeleteResponse deleteResponse = (DeleteResponse)ldapConnection.SendRequest(deleteRequest);
                Log($"Deleted {distinguishedName}");

            }
            catch (Exception err)
            {
                if (showError)
                {
                    Log($"ERROR: Deleting {distinguishedName} {err.Message}");
                }
            }

        }

        static void UpdateHostRecord(string distinguishedName, string oldIP, string newIP)
        {
            // will update A record IP address

            SearchResponse searchResponse;

            LdapConnection ldapConnection = new LdapConnection(localHost);

            try
            {

                SearchRequest searchRequest = new SearchRequest(distinguishedName, $"(objectClass=*)", SearchScope.Base, null);
                searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);
            }
            catch
            {
                // could not find 
                Log($"UpdateHostRecord: {distinguishedName} not found");
                return;
            }


            byte[][] dnsRecord = (byte[][])searchResponse.Entries[0].Attributes["dnsRecord"].GetValues(typeof(byte[]));

            foreach (byte[] byteArray in dnsRecord)
            {
                if (byteArray[2] == 1)        // A record
                {

                    try
                    {

                        string currentIP = byteArray[24].ToString() + "." + byteArray[25].ToString() + "." + byteArray[26].ToString() + "." + byteArray[27].ToString();

                        if (currentIP == oldIP)
                        {
                            byte[] newByteArray = (byte[])byteArray.Clone();

                            string[] ipComponents = newIP.Split('.');

                            newByteArray[24] = byte.Parse(ipComponents[0]);
                            newByteArray[25] = byte.Parse(ipComponents[1]);
                            newByteArray[26] = byte.Parse(ipComponents[2]);
                            newByteArray[27] = byte.Parse(ipComponents[3]);

                            ModifyRequest modifyRequest = new ModifyRequest(searchResponse.Entries[0].DistinguishedName);

                            {
                                // remove the current value with oldIP address
                                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                                mod.Name = "dnsRecord";
                                mod.Operation = DirectoryAttributeOperation.Delete;
                                mod.Add(byteArray);
                                modifyRequest.Modifications.Add(mod);
                            }

                            {
                                // add the current value with newIP address
                                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                                mod.Name = "dnsRecord";
                                mod.Operation = DirectoryAttributeOperation.Add;
                                mod.Add(newByteArray);
                                modifyRequest.Modifications.Add(mod);
                            }

                            ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);

                            Log($"Updated IP address {oldIP}-->{newIP} in {distinguishedName}");


                        }

                    }
                    catch (Exception err)
                    {
                        Log($"ERROR: Updating IP address {oldIP}-->{newIP} in {distinguishedName} {err.Message}");

                    }
                }
            }


            ldapConnection.Dispose();


        }

        static void RemoveHostRecord(string distinguishedName, string oldIP)
        {
            // will remove A record given the IP address

            SearchResponse searchResponse;

            LdapConnection ldapConnection = new LdapConnection(localHost);

            try
            {

                SearchRequest searchRequest = new SearchRequest(distinguishedName, $"(objectClass=*)", SearchScope.Base, null);
                searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);
            }
            catch
            {
                // could not find 
                Log($"RemoveHostRecord: {distinguishedName} not found");
                return;
            }


            byte[][] dnsRecord = (byte[][])searchResponse.Entries[0].Attributes["dnsRecord"].GetValues(typeof(byte[]));

            foreach (byte[] byteArray in dnsRecord)
            {
                if (byteArray[2] == 1)        // A record
                {

                    try
                    {

                        string currentIP = byteArray[24].ToString() + "." + byteArray[25].ToString() + "." + byteArray[26].ToString() + "." + byteArray[27].ToString();

                        if (currentIP == oldIP)
                        {


                            ModifyRequest modifyRequest = new ModifyRequest(searchResponse.Entries[0].DistinguishedName);

                            {
                                // remove the current value with oldIP address
                                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                                mod.Name = "dnsRecord";
                                mod.Operation = DirectoryAttributeOperation.Delete;
                                mod.Add(byteArray);
                                modifyRequest.Modifications.Add(mod);
                            }



                            ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);

                            Log($"Removed IP address {oldIP} from {distinguishedName}");


                        }

                    }
                    catch (Exception err)
                    {
                        Log($"ERROR: Removing IP address {oldIP} from {distinguishedName} {err.Message}");

                    }
                }
            }


            ldapConnection.Dispose();


        }

        static void RemoveHostRecord(string distinguishedName, List<string> whiteList)
        {
            // will remove A record if not in white list of IP addresses 

            SearchResponse searchResponse;

            LdapConnection ldapConnection = new LdapConnection(localHost);

            try
            {

                SearchRequest searchRequest = new SearchRequest(distinguishedName, $"(objectClass=*)", SearchScope.Base, null);
                searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);
            }
            catch
            {
                // could not find 
                Log($"RemoveHostRecord: {distinguishedName} not found");
                return;
            }


            byte[][] dnsRecord = (byte[][])searchResponse.Entries[0].Attributes["dnsRecord"].GetValues(typeof(byte[]));

            string currentIP = ""; ;

            foreach (byte[] byteArray in dnsRecord)
            {
                if (byteArray[2] == 1)        // A record
                {

                    try
                    {

                        currentIP = byteArray[24].ToString() + "." + byteArray[25].ToString() + "." + byteArray[26].ToString() + "." + byteArray[27].ToString();

                        if (!whiteList.Contains(currentIP))
                        {


                            ModifyRequest modifyRequest = new ModifyRequest(searchResponse.Entries[0].DistinguishedName);

                            {
                                // remove the current value with oldIP address
                                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                                mod.Name = "dnsRecord";
                                mod.Operation = DirectoryAttributeOperation.Delete;
                                mod.Add(byteArray);
                                modifyRequest.Modifications.Add(mod);
                            }



                            ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);

                            Log($"Removed IP address {currentIP} from {distinguishedName}");


                        }

                    }
                    catch (Exception err)
                    {
                        Log($"ERROR: Removing IP address {currentIP} from {distinguishedName} {err.Message}");

                    }
                }
            }


            ldapConnection.Dispose();


        }

        static void RemoveSRVRecord(string dnsHostName, string distinguishedName, string filter = null)
        {
            List<SearchResultEntry> results = new List<SearchResultEntry>();
            SearchResponse searchResponse;

            string searchFilter = "(|(name=_kerberos*)(name=_ldap*)(name=_kpasswd*)(name=_gc*))";

            if (!string.IsNullOrEmpty(filter))
            {
                searchFilter = filter;
            }

            // assuming 1000 page size
            LdapConnection ldapConnection = new LdapConnection(localHost);
            SearchRequest searchRequest = new SearchRequest(distinguishedName, searchFilter, SearchScope.OneLevel, null);
            System.DirectoryServices.Protocols.PageResultRequestControl pagedRequestControl = new System.DirectoryServices.Protocols.PageResultRequestControl(1000);
            searchRequest.Controls.Add(pagedRequestControl);

            while (true)
            {
                searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);

                foreach (SearchResultEntry entry in searchResponse.Entries)
                {
                    results.Add(entry);
                }


                foreach (System.DirectoryServices.Protocols.DirectoryControl control in searchResponse.Controls)
                {
                    if (control is System.DirectoryServices.Protocols.PageResultResponseControl)
                    {
                        pagedRequestControl.Cookie = ((System.DirectoryServices.Protocols.PageResultResponseControl)control).Cookie;
                        break;
                    }
                }

                if (pagedRequestControl.Cookie.Length == 0) break;

            }

            if (results.Count == 0)
            {
                return;
            }


            foreach (SearchResultEntry entry in results)
            {

                byte[][] dnsRecord = (byte[][])entry.Attributes["dnsRecord"].GetValues(typeof(byte[]));

                foreach (byte[] byteArray in dnsRecord)
                {

                    if (byteArray[2] == 33)        // SRV record
                    {
                        string fqdn = DecodeFQDN(byteArray, 30);

                        if (fqdn.ToLower() == dnsHostName.ToLower())
                        {


                            /*
                             * Cannot use below as these are stored in big endian 
                               int dnsPriority = BitConverter.ToInt16(byteArray, 24);
                               int dnsWeight = BitConverter.ToInt16(byteArray, 26);
                               int dnsPort = BitConverter.ToInt16(byteArray, 28);

                               so below is quicker rather than reversing the bytes

                            */

                            int dnsPriority = byteArray[24] * 256 + byteArray[25];
                            int dnsWeight = byteArray[26] * 256 + byteArray[27];
                            int dnsPort = byteArray[28] * 256 + byteArray[29];

                            // if there was only value and we're remove that, just delete the object
                            if (dnsRecord.Count() == 1)
                            {

                                try
                                {
                                    DeleteRequest deleteRequest = new DeleteRequest(entry.DistinguishedName);
                                    DeleteResponse deleteResponse = (DeleteResponse)ldapConnection.SendRequest(deleteRequest);
                                    Log($"Deleted {entry.DistinguishedName}");
                                }
                                catch
                                {
                                    Log($"ERROR: Deleting {entry.DistinguishedName}");
                                }

                            }
                            else
                            {
                                try
                                {
                                    ModifyRequest modifyRequest = new ModifyRequest(entry.DistinguishedName);

                                    DirectoryAttributeModification mod = new DirectoryAttributeModification();
                                    mod.Name = "dnsRecord";
                                    mod.Operation = DirectoryAttributeOperation.Delete;
                                    mod.Add(byteArray);
                                    modifyRequest.Modifications.Add(mod);

                                    ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);

                                    Log($"Removed SRV record [{dnsPriority}] [{dnsWeight}] [{dnsPort}] {dnsHostName} from {entry.DistinguishedName}");
                                }
                                catch (Exception err)
                                {
                                    Log($"ERROR: Removing SRV record [{dnsPriority}] [{dnsWeight}] [{dnsPort}] {dnsHostName} from {entry.DistinguishedName} {err.Message}");
                                    return;
                                }
                            }
                        }
                    }
                }
            }
        }

        static void ChangeKerberosWeight(string dnsHostName, string distinguishedName, int weight)
        {
            // will change the weight on the dnshostname to new weight


            /*
             * 
             * SRV records indicate the "priority" and "weight" of the various servers they list. The "priority" value in an SRV record enables administrators to prioritize one server that supports the given service over another. A server with a lower priority value will receive more traffic than other servers. However, the "weight" value is similar: a server with a higher weight will receive more traffic than other servers with the same priority.

                The main difference between them is that priority is looked at first. If there are three servers, Server A, Server B, and Server C, and they have respective priorities of 10, 20, and 30, then their "weight" does not matter. The service will always query Server A first.

                But suppose Servers A, B, and C all have a priority of 10 — how will a service choose between them? This is where weight becomes a factor: if Server A has a "weight" value of 5 and Servers B and C have a "weight" value of 3 and 2, Server A will receive the most traffic, Server B will receive the second-most traffic, and Server C the third-most.
             
             */

            List<SearchResultEntry> results = new List<SearchResultEntry>();
            SearchResponse searchResponse;

            string searchFilter = "(name=_kerberos*)";



            // assuming 1000 page size
            LdapConnection ldapConnection = new LdapConnection(localHost);
            SearchRequest searchRequest = new SearchRequest(distinguishedName, searchFilter, SearchScope.OneLevel, null);
            System.DirectoryServices.Protocols.PageResultRequestControl pagedRequestControl = new System.DirectoryServices.Protocols.PageResultRequestControl(1000);
            searchRequest.Controls.Add(pagedRequestControl);

            while (true)
            {
                searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);

                foreach (SearchResultEntry entry in searchResponse.Entries)
                {
                    results.Add(entry);
                }


                foreach (System.DirectoryServices.Protocols.DirectoryControl control in searchResponse.Controls)
                {
                    if (control is System.DirectoryServices.Protocols.PageResultResponseControl)
                    {
                        pagedRequestControl.Cookie = ((System.DirectoryServices.Protocols.PageResultResponseControl)control).Cookie;
                        break;
                    }
                }

                if (pagedRequestControl.Cookie.Length == 0) break;

            }

            if (results.Count == 0)
            {
                return;
            }


            foreach (SearchResultEntry entry in results)
            {

                byte[][] dnsRecord = (byte[][])entry.Attributes["dnsRecord"].GetValues(typeof(byte[]));

                foreach (byte[] byteArray in dnsRecord)
                {

                    if (byteArray[2] == 33)        // SRV record
                    {
                        string fqdn = DecodeFQDN(byteArray, 30);

                        if (fqdn.ToLower() == dnsHostName.ToLower())
                        {

                            List<byte> newByteArray = new List<byte>();

                            foreach (byte b in byteArray)
                            {
                                newByteArray.Add(b);
                            }


                            /*
                             * Cannot use below as these are stored in big endian 
                               int dnsPriority = BitConverter.ToInt16(byteArray, 24);
                               int dnsWeight = BitConverter.ToInt16(byteArray, 26);
                               int dnsPort = BitConverter.ToInt16(byteArray, 28);

                               so below is quicker rather than reversing the bytes

                            */

                            int dnsPriority = byteArray[24] * 256 + byteArray[25];
                            int dnsWeight = byteArray[26] * 256 + byteArray[27];
                            int dnsPort = byteArray[28] * 256 + byteArray[29];

                            newByteArray[27] = (byte)weight;

                            // add newbytearray
                            // remove old one


                            try
                            {
                                ModifyRequest modifyRequest = new ModifyRequest(entry.DistinguishedName);

                                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                                mod.Name = "dnsRecord";
                                mod.Operation = DirectoryAttributeOperation.Add;
                                mod.Add(newByteArray.ToArray());
                                modifyRequest.Modifications.Add(mod);

                                ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);

                                Log($"Added SRV record [{dnsPriority}] [{weight}] [{dnsPort}] {dnsHostName} from {entry.DistinguishedName}");
                            }
                            catch (Exception err)
                            {
                                Log($"ERROR: Adding SRV record [{dnsPriority}] [{weight}] [{dnsPort}] {dnsHostName} from {entry.DistinguishedName} {err.Message}");

                            }


                            try
                            {
                                ModifyRequest modifyRequest = new ModifyRequest(entry.DistinguishedName);

                                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                                mod.Name = "dnsRecord";
                                mod.Operation = DirectoryAttributeOperation.Delete;
                                mod.Add(byteArray);
                                modifyRequest.Modifications.Add(mod);

                                ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);

                                Log($"Removed SRV record [{dnsPriority}] [{dnsWeight}] [{dnsPort}] {dnsHostName} from {entry.DistinguishedName}");
                            }
                            catch (Exception err)
                            {
                                Log($"ERROR: Removing SRV record [{dnsPriority}] [{dnsWeight}] [{dnsPort}] {dnsHostName} from {entry.DistinguishedName} {err.Message}");

                            }


                        }
                    }
                }
            }


        }

        static void AddHostRecord(string zone, string dnsHostName, string ipAddress)
        {
            try
            {

                ManagementScope managementScope = new ManagementScope(@"\\.\Root\MicrosoftDNS");
                managementScope.Connect();

                ManagementClass mgmtClass = new ManagementClass(managementScope, new ManagementPath("MicrosoftDNS_AType"), null);

                ManagementBaseObject mgmtParams = mgmtClass.GetMethodParameters("CreateInstanceFromPropertyData");
                mgmtParams["DnsServerName"] = restoredForestInfo.hostNameOnly;
                mgmtParams["ContainerName"] = zone;
                mgmtParams["OwnerName"] = dnsHostName;
                mgmtParams["IPAddress"] = ipAddress;

                mgmtClass.InvokeMethod("CreateInstanceFromPropertyData", mgmtParams, null);

                Log($"Created A record {dnsHostName} {ipAddress} in {zone}");
            }
            catch (Exception err)
            {
                Log($"ERROR: Creating A record {dnsHostName} {ipAddress} in {zone} {err.Message}");
            }
        }

        static void DeleteHostRecord(string distinguishedName)
        {
            // If you just perform a hard delete om the dnsNode object in AD, it will still be in the DNS cache and still show in the DNS console
            // and if you subsequently try to update the host record after a delete, it will in fact modify the current record 
            // this means for a host record, if you delete and then add a ip, you actually have the old ip and the new ip
            // which is not what you want !

            // so need to perform a dns delete

            /*
                00 set to 8
                02 set to 0
                08 to 11 - this is the serial number, increase this
                12 to 15 - this is the TTL, set to 0
                24-31 this the the datetime it was deleted, stored as uint64 and in filetime format UTC
             */

            LdapConnection ldapConnection = new LdapConnection(localHost);

            SearchRequest searchRequest = new SearchRequest(distinguishedName, "(objectClass=*)", SearchScope.Base, null);
            SearchResponse searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);

            byte[][] dnsRecord = (byte[][])searchResponse.Entries[0].Attributes["dnsRecord"].GetValues(typeof(byte[]));

            // there should only be one, but if more than 1, take first one
            byte[] newDnsRecord = dnsRecord[0];

            newDnsRecord[0] = 8;
            newDnsRecord[2] = 0;

            // increase serial number
            int serialNo = BitConverter.ToInt32(newDnsRecord, 8);
            byte[] newSerialNoAsBytes = BitConverter.GetBytes(serialNo + 1);
            newDnsRecord[8] = newSerialNoAsBytes[0];
            newDnsRecord[9] = newSerialNoAsBytes[1];
            newDnsRecord[10] = newSerialNoAsBytes[2];
            newDnsRecord[11] = newSerialNoAsBytes[3];

            // ttl
            newDnsRecord[12] = 0;
            newDnsRecord[13] = 0;
            newDnsRecord[14] = 0;
            newDnsRecord[15] = 0;

            // set time deleted
            Int64 deletedTime = DateTime.UtcNow.ToFileTimeUtc();
            byte[] deletedTimeBytes = BitConverter.GetBytes(deletedTime);

            newDnsRecord[24] = deletedTimeBytes[0];
            newDnsRecord[25] = deletedTimeBytes[1];
            newDnsRecord[26] = deletedTimeBytes[2];
            newDnsRecord[27] = deletedTimeBytes[3];
            newDnsRecord[28] = deletedTimeBytes[4];
            newDnsRecord[29] = deletedTimeBytes[5];
            newDnsRecord[30] = deletedTimeBytes[6];
            newDnsRecord[31] = deletedTimeBytes[7];

            ModifyRequest modifyRequest = new ModifyRequest(distinguishedName);

            {
                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                mod.Name = "dNSTombstoned";
                mod.Operation = DirectoryAttributeOperation.Replace;
                mod.Add("TRUE");
                modifyRequest.Modifications.Add(mod);
            }

            {
                // delete current dnsRecord
                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                mod.Name = "dnsRecord";
                mod.Operation = DirectoryAttributeOperation.Replace;
                mod.Add((string)null);
                modifyRequest.Modifications.Add(mod);
            }

            {
                // add our updated dnsRecord
                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                mod.Name = "dnsRecord";
                mod.Operation = DirectoryAttributeOperation.Replace;
                mod.Add(newDnsRecord);
                modifyRequest.Modifications.Add(mod);
            }

            ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);

        }

        static void ReloadZone(string zone)
        {
            try
            {
                ManagementScope managementScope = new ManagementScope(@"\\.\Root\MicrosoftDNS");
                managementScope.Connect();

                SelectQuery query = new SelectQuery("MicrosoftDNS_Zone", $"ContainerName='{zone}'");

                ManagementObjectSearcher mgmtSrchr = new ManagementObjectSearcher(managementScope, query);

                ManagementObjectCollection managementObjectCollection = mgmtSrchr.Get();

                foreach (ManagementObject result in managementObjectCollection)
                {
                    result.InvokeMethod("ReloadZone", null, null);
                }

                Log($"Reloaded zone {zone}");
            }
            catch (Exception err)
            {
                Log($"ERROR: Reloading zone {zone} {err.Message}");
            }
        }

        static void UpdateFromDS(string zone)
        {
            try
            {
                ManagementScope managementScope = new ManagementScope(@"\\.\Root\MicrosoftDNS");
                managementScope.Connect();

                SelectQuery query = new SelectQuery("MicrosoftDNS_Zone", $"ContainerName='{zone}'");

                ManagementObjectSearcher mgmtSrchr = new ManagementObjectSearcher(managementScope, query);

                ManagementObjectCollection managementObjectCollection = mgmtSrchr.Get();

                foreach (ManagementObject result in managementObjectCollection)
                {
                    result.InvokeMethod("UpdateFromDS", null, null);
                }

                Log($"UpdateFromDS zone {zone}");
            }
            catch (Exception err)
            {
                Log($"ERROR: UpdateFromDS zone {zone} {err.Message}");
            }
        }

        static void AddNSRecord(string zone, string nsServer)
        {
            try
            {

                ManagementScope managementScope = new ManagementScope(@"\\.\Root\MicrosoftDNS");
                managementScope.Connect();

                ManagementClass mgmtClass = new ManagementClass(managementScope, new ManagementPath("MicrosoftDNS_NSType"), null);

                ManagementBaseObject mgmtParams = mgmtClass.GetMethodParameters("CreateInstanceFromPropertyData");
                mgmtParams["DnsServerName"] = restoredForestInfo.hostNameOnly;
                mgmtParams["ContainerName"] = zone;
                mgmtParams["OwnerName"] = zone;
                mgmtParams["NSHost"] = nsServer;

                mgmtClass.InvokeMethod("CreateInstanceFromPropertyData", mgmtParams, null);

                Log($"Created NS record {nsServer} in {zone}");
            }
            catch (Exception err)
            {
                Log($"ERROR: Creating NS record {nsServer} in {zone} {err.Message}");
            }
        }

        static void AddDelegation(string delegatedDomain, string zone, string nsServer)
        {
            // will add a NS record to delegatedDomain which is in zone
            // e.g. _mdscs is delegated domain in zone root.local
            // will therefore add a NS for this

            Exception err = null;

            for (int i = 0; i < 5; i++)
            {
                try
                {

                    ManagementScope managementScope = new ManagementScope(@"\\.\Root\MicrosoftDNS");
                    managementScope.Connect();

                    ManagementClass mgmtClass = new ManagementClass(managementScope, new ManagementPath("MicrosoftDNS_NSType"), null);

                    ManagementBaseObject mgmtParams = mgmtClass.GetMethodParameters("CreateInstanceFromPropertyData");
                    mgmtParams["DnsServerName"] = restoredForestInfo.hostNameOnly;
                    mgmtParams["ContainerName"] = zone;
                    mgmtParams["OwnerName"] = delegatedDomain;
                    mgmtParams["NSHost"] = nsServer;

                    mgmtClass.InvokeMethod("CreateInstanceFromPropertyData", mgmtParams, null);

                    Log($"Created NS record {nsServer} for {delegatedDomain} in {zone}");
                    return;
                }
                catch (Exception ex)
                {
                    // have seen generic failure happening somethimes
                    err = ex;
                }

                Thread.Sleep(1000);
            }

            Log($"ERROR: Creating NS record {nsServer} for {delegatedDomain} in {zone} {err.Message}");

        }

        static void SetDNSServers(string dnsserver)
        {
            string[] dnsservers = new string[] { dnsserver };


            ManagementScope managementScope = new ManagementScope(@"\\.\root\cimv2");

            try
            {
                managementScope.Connect();
            }
            catch (Exception err)
            {
                Log($"ERROR: Connecting to WMI SetDNSServers {err.Message}");
                return;
            }


            SelectQuery query = new SelectQuery("Win32_NetworkAdapterConfiguration", "IPEnabled='True'");

            ManagementObjectSearcher mgmtSrchr = new ManagementObjectSearcher(managementScope, query);

            ManagementObjectCollection managementObjectCollection = mgmtSrchr.Get();

            foreach (ManagementObject result in managementObjectCollection)
            {
                string adapterName = (string)result["Description"];
                string[] IPAddresses = (string[])result["IPAddress"];
                string[] DNSServers = (string[])result["DNSServerSearchOrder"];
                string[] subnets = (string[])result["IPSubnet"];
                string[] defaultIPGateways = (string[])result["defaultIPGateway"];
                bool dhcpEnabled = (bool)result["DHCPEnabled"];

                ManagementBaseObject inParams = result.GetMethodParameters("SetDNSServerSearchOrder");
                inParams["DNSServerSearchOrder"] = dnsservers;
                ManagementBaseObject outParams = result.InvokeMethod("SetDNSServerSearchOrder", inParams, null);
                int retValue = int.Parse(outParams["returnValue"].ToString());

                if (retValue == 0)
                {
                    // return ReturnCodes.Success;
                }
                else
                {
                    //  Logging.WriteToLogFile($"[{s.server}] Error setting DNS server {retValue}");
                    //  return ReturnCodes.Failed;
                }

            }

            // return ReturnCodes.Failed;
        }

        static void SetConditionalForwarder(string dnsServer)
        {
            RegistryKey keyToWrite = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\DNS\Parameters");
            keyToWrite.SetValue("Forwarders", new string[] { dnsServer }, RegistryValueKind.MultiString);


            // restart DNS for this to be effective
        }

        static void FixADConditionalForwarders(string distinguishedName)
        {

            Log($"Fixing any AD conditional forwarders");

            LdapConnection ldapConnection = new LdapConnection(localHost);


            SearchRequest searchRequest = new SearchRequest(distinguishedName, "(objectClass=dnsZone)", SearchScope.OneLevel, null);
            SearchResponse searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);


            foreach (SearchResultEntry entry in searchResponse.Entries)
            {

                if (!entry.Attributes.Contains("dnsProperty")) continue;

                byte[][] dnsProperty = (byte[][])entry.Attributes["dnsProperty"].GetValues(typeof(byte[]));

                string forwardedZone = CheckIfConditionalForwarder(dnsProperty, entry.DistinguishedName);

                if (forwardedZone != null)
                {
                    foreach (byte[] byteArray in dnsProperty)
                    {

                        if (byteArray[16] == 0x91)
                        {

                            string currentForwarder = byteArray[56].ToString() + "." + byteArray[57].ToString() + "." + byteArray[58].ToString() + "." + byteArray[59].ToString();

                            Log($"Current forwarder for {forwardedZone} is {currentForwarder}");


                            DomainControllerInfo domainController = restoredForestInfo.RestoredDomainControllers.Find(x => x.oldIP == currentForwarder);


                            if (domainController != null)           // found a match based on restored new IP

                            {

                                Log($"New forwarder for {forwardedZone} will be {domainController.newIP}");


                                string[] ipStr = domainController.newIP.Split('.');

                                byte[] newByteArray = byteArray.ToArray();

                                newByteArray[56] = byte.Parse(ipStr[0]);
                                newByteArray[57] = byte.Parse(ipStr[1]);
                                newByteArray[58] = byte.Parse(ipStr[2]);
                                newByteArray[59] = byte.Parse(ipStr[3]);

                                try
                                {
                                    ModifyRequest modifyRequest = new ModifyRequest(entry.DistinguishedName);

                                    // add new forwarder
                                    {
                                        DirectoryAttributeModification mod = new DirectoryAttributeModification();
                                        mod.Name = "dnsProperty";
                                        mod.Operation = DirectoryAttributeOperation.Add;
                                        mod.Add(newByteArray);

                                        modifyRequest.Modifications.Add(mod);
                                    }

                                    //remove previous forwarder
                                    {
                                        DirectoryAttributeModification mod = new DirectoryAttributeModification();
                                        mod.Name = "dnsProperty";
                                        mod.Operation = DirectoryAttributeOperation.Delete;
                                        mod.Add(byteArray);

                                        modifyRequest.Modifications.Add(mod);
                                    }


                                    ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);

                                    Log($"Updated forwarder for {forwardedZone} from {domainController.oldIP} to {domainController.newIP}");

                                }
                                catch (Exception err)
                                {
                                    Log($"ERROR: Updating forwarder {err.Message}");
                                }


                            }
                            else
                            {
                                // the original forwarder was not restored, so we'll find another 
                                // domain controller of the same domain that was restored and make that the forwarder

                                DomainControllerInfo domainController1 = restoredForestInfo.RestoredDomainControllers.Find(x => x.domainDNS == forwardedZone);


                                if (domainController1 != null)
                                {

                                    Log($"New forwarder for {forwardedZone} will be {domainController1.newIP}");

                                    string[] newIP = domainController1.newIP.Split('.');

                                    byte[] newByteArray = byteArray.ToArray();

                                    newByteArray[56] = byte.Parse(newIP[0]);
                                    newByteArray[57] = byte.Parse(newIP[1]);
                                    newByteArray[58] = byte.Parse(newIP[2]);
                                    newByteArray[59] = byte.Parse(newIP[3]);

                                    try
                                    {
                                        ModifyRequest modifyRequest = new ModifyRequest(entry.DistinguishedName);

                                        // add new forwarder
                                        {
                                            DirectoryAttributeModification mod = new DirectoryAttributeModification();
                                            mod.Name = "dnsProperty";
                                            mod.Operation = DirectoryAttributeOperation.Add;
                                            mod.Add(newByteArray);

                                            modifyRequest.Modifications.Add(mod);
                                        }

                                        //remove previous forwarder
                                        {
                                            DirectoryAttributeModification mod = new DirectoryAttributeModification();
                                            mod.Name = "dnsProperty";
                                            mod.Operation = DirectoryAttributeOperation.Delete;
                                            mod.Add(byteArray);

                                            modifyRequest.Modifications.Add(mod);
                                        }


                                        ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);

                                        Log($"WARN:Found alternative forwarder for {forwardedZone} updated forwarder from {currentForwarder} to {newIP}");

                                    }
                                    catch (Exception err)
                                    {
                                        Log($"ERROR: Updating alternative forwarder {err.Message}");
                                    }
                                }
                                else
                                {
                                    Log($"ERROR: No suitable conditional forwarder found for {currentForwarder}");
                                }

                            }
                        }

                    }
                }
            }
        }


        static string CheckIfConditionalForwarder(byte[][] dnsProperty, string distinguishedName)
        {

            foreach (byte[] byteArray in dnsProperty)
            {

                if (byteArray[16] == 0x1 && byteArray[20] == 0x4)        // zone is a forwarder
                {

                    string forwardedZone = distinguishedName.ToLower();
                    forwardedZone = forwardedZone.Replace($",cn=microsoftdns,dc=forestdnszones,{restoredForestInfo.rootDomainNamingContext.ToLower()}", "");
                    forwardedZone = forwardedZone.Replace($",cn=microsoftdns,dc=domaindnszones,{restoredForestInfo.defaultNamingContext.ToLower()}", "");
                    forwardedZone = forwardedZone.Replace("dc=", "").Trim();
                    return forwardedZone;
                }
            }

            return null;
        }

        #endregion

        static void ForceReplication()
        {
            Log("Forcing replication");
            ExecuteProcessWithOutput(@"c:\windows\system32\repadmin.exe", "/syncall /APedS", "Repadmin.txt");

        }

        static void ForceReplication2()
        {


            System.DirectoryServices.ActiveDirectory.SyncFromAllServersOptions syncOptions = System.DirectoryServices.ActiveDirectory.SyncFromAllServersOptions.CrossSite |
                                                                                             System.DirectoryServices.ActiveDirectory.SyncFromAllServersOptions.PushChangeOutward |
                                                                                             System.DirectoryServices.ActiveDirectory.SyncFromAllServersOptions.SkipInitialCheck;

            System.DirectoryServices.ActiveDirectory.DirectoryContext directoryContext = new System.DirectoryServices.ActiveDirectory.DirectoryContext(System.DirectoryServices.ActiveDirectory.DirectoryContextType.DirectoryServer, restoredForestInfo.dnsHostName);

            System.DirectoryServices.ActiveDirectory.DomainController dc = System.DirectoryServices.ActiveDirectory.DomainController.GetDomainController(directoryContext);


            foreach (string namingContext in restoredForestInfo.namingContexts)
            {
                try
                {
                    dc.SyncReplicaFromAllServers(namingContext, syncOptions);
                    Log($"Replicated {namingContext}");
                }
                catch (Exception err)
                {
                    Log($"{namingContext}  {err.Message}");

                }
            }

            dc.Dispose();

        }

        static void ForceReplication3()
        {
            for (int i = 0; i < 5; i++)
            {
                foreach (DomainControllerInfo domainController in restoredForestInfo.RestoredDomainControllers)
                {
                    ExecuteProcessWithOutput(@"c:\windows\system32\repadmin.exe", $"/syncall {domainController.dnsHostName} {restoredForestInfo.defaultNamingContext} /APedS", "repadmin.txt");
                    Thread.Sleep(1000);
                }

                Thread.Sleep(5000);
            }
        }

        static void ForceReplicationForPartion(string partition, string destinationServer)
        {

            try
            {
                System.DirectoryServices.ActiveDirectory.SyncFromAllServersOptions syncOptions = System.DirectoryServices.ActiveDirectory.SyncFromAllServersOptions.CrossSite |
                                                                                                 System.DirectoryServices.ActiveDirectory.SyncFromAllServersOptions.PushChangeOutward |
                                                                                                 System.DirectoryServices.ActiveDirectory.SyncFromAllServersOptions.SkipInitialCheck;

                System.DirectoryServices.ActiveDirectory.DirectoryContext directoryContext = new System.DirectoryServices.ActiveDirectory.DirectoryContext(System.DirectoryServices.ActiveDirectory.DirectoryContextType.DirectoryServer, destinationServer);

                System.DirectoryServices.ActiveDirectory.DomainController dc = System.DirectoryServices.ActiveDirectory.DomainController.GetDomainController(directoryContext);

                dc.SyncReplicaFromAllServers(partition, syncOptions);

            }
            catch
            {
            }
        }

        static void ReadServers()
        {

            if (!File.Exists(@"C:\RestoreFromIFM\servers.txt"))
            {
                Log(@"Cannot find C:\RestoreFromIFM\servers.txt");
                StepFailed();
                return;

            }


            RestoredServers.Clear();

            string[] lines = System.IO.File.ReadAllLines(@"C:\RestoreFromIFM\servers.txt");

            foreach (string line in lines)
            {

                if (line.Trim() == "" || line.StartsWith("#")) continue;

                string[] tmpArray = line.Split(',');

                RestoredServer s = new RestoredServer();

                s.dnsHostName = tmpArray[0].Trim().ToLower();
                s.oldIP = tmpArray[1].Trim();
                s.newIP = tmpArray[2].Trim();


                tmpArray = s.dnsHostName.Split('.');
                s.hostNameOnly = tmpArray[0];
                s.domainDNS = s.dnsHostName.Replace(s.hostNameOnly + ".", "");

                tmpArray = s.domainDNS.Split('.');
                s.domainOnly = tmpArray[0];

                RestoredServers.Add(s);
            }


            if (RestoredServers.Count == 0)
            {
                Log($"No servers found in file !");
                StepFailed();
            }
        }

        static bool DelegationExists(string zone, string delegation)
        {
            // Checks if a delegation for the child domain exists in this domain

            string dn = $"DC={zone},CN=MicrosoftDNS,DC=DomainDnsZones,DC={zone.Replace(".", ",DC=")}";

            LdapConnection ldapConnection = new LdapConnection(localHost);

            SearchRequest searchRequest = new SearchRequest(dn, $"(name={delegation})", SearchScope.OneLevel, null);
            SearchResponse searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);

            if (searchResponse.Entries.Count == 1)
            {
                return true;
            }
            else
            {
                return false;
            }


        }

        static bool RestartService(string serviceName)
        {
            if (!StopService(serviceName))
            {
                return false;
            }

            if (!StartService(serviceName))
            {
                return false;
            }

            return true;
        }


        static void SetServiceTimeout()
        {
            // needs a reboot
            // TODO: undo value

            RegistryKey keyToWrite = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control");
            keyToWrite.SetValue("ServicesPipeTimeout", 60000);     // 60 secs 
            keyToWrite.Close();
            Log($"Setting service timeout to 60 seconds");
        }

        static bool StopService(string serviceName, bool meetsCondition = true)
        {
            if (!meetsCondition) return true;

            // HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\ServicesPipeTimeout ? 
            // https://stackoverflow.com/questions/13454054/what-is-the-maximum-time-windows-service-wait-to-process-stop-request-and-how-to

            // wait 10 mins for services to stop..this does not seem to work !



            try
            {
                ServiceController sc = new ServiceController(serviceName);

                if (sc.Status == ServiceControllerStatus.Stopped)
                {
                    Log($"{serviceName} already stopped");
                    return true;
                }

                sc.Stop();

                Log($"Stopping {serviceName}");
                sc.WaitForStatus(ServiceControllerStatus.Stopped, new TimeSpan(0, 10, 0));
                return true;
            }
            catch
            {
                Log($"ERROR:{serviceName} did not stop in a timely manner");
                return false;
            }
        }

        static bool StartService(string serviceName)
        {


            try
            {
                ServiceController sc = new ServiceController(serviceName);

                if (sc.Status == ServiceControllerStatus.Running)
                {
                    Log($"{serviceName} already running");
                    return true;
                }

                sc.Start();

                Log($"Starting {serviceName}");
                sc.WaitForStatus(ServiceControllerStatus.Running, new TimeSpan(0, 10, 0));
                return true;
            }
            catch
            {
                Log($"ERROR:{serviceName} did not start in a timely manner");
                return false;
            }


        }

        static void DisableService(string serviceName, bool meetsCondition = true)
        {
            if (!meetsCondition) return;

            Log($"Disabling service {serviceName}");
            ExecuteProcess(@"c:\windows\system32\sc.exe", $"config {serviceName} start= disabled");
        }

        static void EnableService(string serviceName, bool meetsCondition = true)
        {
            if (!meetsCondition) return;

            Log($"Enabling service {serviceName}");
            ExecuteProcess(@"c:\windows\system32\sc.exe", $"config {serviceName} start= auto");
        }

        static void DisableNetBios()
        {
            //https://241931348f64b1d1.wordpress.com/2010/04/21/how-to-disable-netbios-via-command-line-on-windows/
            //https://github.com/hvs-consulting/disable-netbios/blob/main/disable_netbios.ps1


            RegistryKey interfaces = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces");

            foreach (string interfaceName in interfaces.GetSubKeyNames())
            {
                RegistryKey thisInterface = interfaces.OpenSubKey(interfaceName, true);


                //Save the name and value in registry
                RegistryKey restoreFromIFMKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\RestoreFromIFM", true);
                int currentValue = (int)thisInterface.GetValue("NetbiosOptions");
                restoreFromIFMKey.SetValue(interfaceName, currentValue);
                restoreFromIFMKey.Close();

                // change to 2 i.e. disabled
                thisInterface.SetValue("NetbiosOptions", 2);
                thisInterface.Close();
                Log($"Disabling Netbios over TCP/IP on {interfaceName}");
            }


        }

        static void RestoreNetbiosSettings()
        {
            try
            {
                RegistryKey interfaces = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\RestoreFromIFM");

                foreach (string valueName in interfaces.GetValueNames())
                {
                    if (valueName.StartsWith("Tcp"))
                    {
                        int previousValue = (int)interfaces.GetValue(valueName);
                        RegistryKey t = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces\{valueName}", true);
                        t.SetValue("NetbiosOptions", previousValue);
                        Log($"Restoring Netbios over TCP/IP on {valueName}");
                    }
                }
            }
            catch
            {

            }

        }

        static int GetPromotionExitCode()
        {
            if (File.Exists($@"c:\windows\debug\dcpromoui.log"))
            {

                string[] lines = System.IO.File.ReadAllLines($@"c:\windows\debug\dcpromoui.log");

                //read the last line, should be closing log if process finished
                string lastLine = lines[lines.Count() - 1].ToLower();
                if (!lastLine.Contains("closing log"))
                {
                    return -1;
                }

                // read from bottom up
                for (int i = lines.Count() - 1; i > -1; i--)
                {
                    string line = lines[i].ToLower();
                    if (line.Contains("exit code is"))
                    {
                        string code = line.Substring(line.IndexOf("exit code is ")).Trim().Replace("exit code is ", "").Trim();
                        return Int32.Parse(code);

                    }

                }



            }

            // could not find dcpromoui.log
            // error in dcpromo command so it never ran correctly
            return -1;



        }

        static string GetLocalIPAdddress()
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
                            localIPAddress = ipAddress;
                            return ipAddress;
                        }

                    }
                }
            }

            Log("Cannot determine local IP address");
            StepFailed();

            return null;
        }

        static void SetReplicationEpochInAD()
        {
            LdapConnection ldapConnection = new LdapConnection(localHost);

            try
            {

                SearchRequest searchRequest = new SearchRequest($"CN=Sites,{ditInfo.ConfigurationNamingContext}", $"(&(objectClass=Server)(dnsHostName={ditInfo.dnsHostName}))", SearchScope.Subtree, null);
                SearchResponse searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);

                string serverObject = searchResponse.Entries[0].DistinguishedName;

                ModifyRequest modifyRequest = new ModifyRequest();
                modifyRequest.DistinguishedName = $"CN=NTDS Settings,{serverObject}";

                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                mod.Operation = DirectoryAttributeOperation.Replace;
                mod.Name = "msDS-ReplicationEpoch";
                mod.Add(settings.commonEpoch.ToString());
                modifyRequest.Modifications.Add(mod);

                ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);

                Log($"ReplicationEpoch set to {settings.commonEpoch}");

            }
            catch (Exception err)
            {
                Log($"ERROR: setting msDS-ReplicationEpoch {err.Message}");
            }

        }

        static void IsolateDomainController()
        {


            while (true)
            {
                try
                {
                    Log($"Waiting for ldap services to be available");
                    LdapConnection ldapConnectionRootDSE = new LdapConnection(localHost);

                    SearchRequest searchRootDSE = new SearchRequest(null, "(objectClass=*)", SearchScope.Base, new string[] { "dnsHostName" });
                    SearchResponse response = (SearchResponse)ldapConnectionRootDSE.SendRequest(searchRootDSE, new TimeSpan(0, 0, 1));

                    if (((string)response.Entries[0].Attributes["dnsHostName"][0]).ToLower() == restoredForestInfo.dnsHostName)
                    {

                        DomainControllerInfo domainController = restoredForestInfo.RestoredDomainControllers.Find(x => x.dnsHostName == restoredForestInfo.dnsHostName);

                        ModifyRequest modifyRequest = new ModifyRequest($"{domainController.dsServiceName}");
                        DirectoryAttributeModification mod = new DirectoryAttributeModification();
                        mod.Operation = DirectoryAttributeOperation.Replace;
                        mod.Name = "msDS-ReplicationEpoch";
                        mod.Add(settings.isolationEpoch.ToString());
                        modifyRequest.Modifications.Add(mod);

                        ModifyResponse modifyResponse = (ModifyResponse)ldapConnectionRootDSE.SendRequest(modifyRequest);
                        Log($"Isolated domain controller with epoch {settings.isolationEpoch}");
                        return;

                    }
                }
                catch
                {

                }
            }





        }

        static void RunKCC(string dnsHostName)
        {
            Log($"Running KCC");

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    System.DirectoryServices.ActiveDirectory.DirectoryContext directoryContext = new System.DirectoryServices.ActiveDirectory.DirectoryContext(System.DirectoryServices.ActiveDirectory.DirectoryContextType.DirectoryServer, dnsHostName);
                    System.DirectoryServices.ActiveDirectory.DomainController domainController = System.DirectoryServices.ActiveDirectory.DomainController.GetDomainController(directoryContext);
                    domainController.CheckReplicationConsistency();
                    Thread.Sleep(10000);        // allow 10 secs for kcc to process new topology
                    return;
                }
                catch
                {
                    Thread.Sleep(5000);
                }
            }
        }

        static void MakeDFSRAuthoratativeOnPDC()
        {

            if (restoredForestInfo.ThisDomainController.isPDC)
            {
                try
                {
                    LdapConnection ldapConnection = new LdapConnection(localHost);
                    ModifyRequest modifyRequest = new ModifyRequest();
                    modifyRequest.DistinguishedName = $"CN=SYSVOL Subscription,CN=Domain System Volume,CN=DFSR-LocalSettings,{restoredForestInfo.PDCMaster.serverReference}";
                    DirectoryAttributeModification mod = new DirectoryAttributeModification();
                    mod.Name = "msDFSR-Options";
                    mod.Operation = DirectoryAttributeOperation.Replace;
                    mod.Add("1");
                    modifyRequest.Modifications.Add(mod);

                    ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);

                    Log($"{restoredForestInfo.PDCMaster.dnsHostName} made authoratative for SYSVOL (msDFSR-Options set to 1)");
                }
                catch (Exception err)
                {
                    Log($"ERROR: setting msDFSR-Options set to 1 {err.Message}");
                }

            }



        }

        static void SeizeFSMOs()
        {
            LdapConnection ldapConnection = new LdapConnection(localHost);


            // NOTE: We only actually seize the role on the server that is designated to receive the role



            if (restoredForestInfo.seizeSchemaMaster && restoredForestInfo.ThisDomainController.dsServiceName == restoredForestInfo.SchemaMaster.dsServiceName)
            {

                ModifyRequest modifyRequest = new ModifyRequest();
                modifyRequest.DistinguishedName = $"{restoredForestInfo.schemaNamingContext}";

                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                mod.Name = "fSMORoleOwner";
                mod.Operation = DirectoryAttributeOperation.Replace;
                mod.Add($"{restoredForestInfo.ThisDomainController.dsServiceName}");
                modifyRequest.Modifications.Add(mod);

                try
                {
                    ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);
                    Log($"Schema Master seized to {restoredForestInfo.ThisDomainController.dnsHostName}");
                }
                catch (Exception err)
                {
                    Log($"ERROR: Seizing schema master {err.Message}");
                }
            }




            if (restoredForestInfo.seizeNamingMaster && restoredForestInfo.ThisDomainController.dsServiceName == restoredForestInfo.NamingMaster.dsServiceName)
            {

                ModifyRequest modifyRequest = new ModifyRequest();
                modifyRequest.DistinguishedName = $"CN=Partitions,{restoredForestInfo.configurationNamingContext}";

                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                mod.Name = "fSMORoleOwner";
                mod.Operation = DirectoryAttributeOperation.Replace;
                mod.Add($"{restoredForestInfo.ThisDomainController.dsServiceName}");
                modifyRequest.Modifications.Add(mod);

                try
                {
                    ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);
                    Log($"Naming Master seized to {restoredForestInfo.ThisDomainController.dnsHostName}");
                }
                catch (Exception err)
                {
                    Log($"ERROR: Seizing naming master {err.Message}");
                }
            }



            if (restoredForestInfo.seizePDCMaster && restoredForestInfo.ThisDomainController.dsServiceName == restoredForestInfo.PDCMaster.dsServiceName)
            {

                ModifyRequest modifyRequest = new ModifyRequest();
                modifyRequest.DistinguishedName = $"{restoredForestInfo.defaultNamingContext}";


                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                mod.Name = "fSMORoleOwner";
                mod.Operation = DirectoryAttributeOperation.Replace;
                mod.Add($"{restoredForestInfo.ThisDomainController.dsServiceName}");
                modifyRequest.Modifications.Add(mod);

                try
                {
                    ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);
                    Log($"PDC Master seized to {restoredForestInfo.ThisDomainController.dnsHostName}");
                }
                catch (Exception err)
                {
                    Log($"ERROR: Seizing pdc master {err.Message}");
                }
            }



            // For the RID master, if it was restored, we need to force an update as otherwise get errors when trying to create a new account
            // so regardless of it being restored or not, will force an update
            if (restoredForestInfo.ThisDomainController.dsServiceName == restoredForestInfo.RIDMaster.dsServiceName)
            {

                ModifyRequest modifyRequest = new ModifyRequest();
                modifyRequest.DistinguishedName = $"CN=RID Manager$,CN=System,{restoredForestInfo.defaultNamingContext}";

                DirectoryControl LDAP_SERVER_FORCE_UPDATE_OID = new DirectoryControl("1.2.840.113556.1.4.1974", null, false, true);
                modifyRequest.Controls.Add(LDAP_SERVER_FORCE_UPDATE_OID);

                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                mod.Name = "fSMORoleOwner";
                mod.Operation = DirectoryAttributeOperation.Replace;
                mod.Add($"{restoredForestInfo.ThisDomainController.dsServiceName}");
                modifyRequest.Modifications.Add(mod);

                try
                {
                    ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);
                    Log($"RID Master seized to {restoredForestInfo.ThisDomainController.dnsHostName}");
                }
                catch (Exception err)
                {
                    Log($"ERROR: Seizing rid master {err.Message}");
                }
            }


            if (restoredForestInfo.seizeInfraMaster && restoredForestInfo.ThisDomainController.dsServiceName == restoredForestInfo.InfraMaster.dsServiceName)
            {


                ModifyRequest modifyRequest = new ModifyRequest();
                modifyRequest.DistinguishedName = $"CN=Infrastructure,{restoredForestInfo.defaultNamingContext}";


                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                mod.Name = "fSMORoleOwner";
                mod.Operation = DirectoryAttributeOperation.Replace;
                mod.Add($"{restoredForestInfo.ThisDomainController.dsServiceName}");
                modifyRequest.Modifications.Add(mod);

                try
                {
                    ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);
                    Log($"Infra Master seized to {restoredForestInfo.ThisDomainController.dnsHostName}");
                }
                catch (Exception err)
                {
                    Log($"ERROR: Seizing infra master {err.Message}");
                }
            }




        }

        static void GetRestoredForestInfo()
        {
            if (File.Exists(@"C:\RestoreFromIFM\restoredForestInfo.xml"))
            {
                // restore restoredForestInfo
                {
                    System.Xml.Serialization.XmlSerializer serializer = new System.Xml.Serialization.XmlSerializer(typeof(RestoredForestInfo));

                    using (System.IO.Stream reader = new System.IO.FileStream(@"C:\RestoreFromIFM\restoredForestInfo.xml", System.IO.FileMode.Open))
                    {
                        restoredForestInfo = (RestoredForestInfo)serializer.Deserialize(reader);
                    }
                }

                return;
            }

            if (!AreFeaturesInstalled(false))
            {
                InstallFeatures();

                // check they installed
                if (!AreFeaturesInstalled(true))
                {
                    Log($"Could not install features required for ADDS");
                    StepFailed();

                }
            }

            MountDIT();

            restoredForestInfo = ReadMountedDIT();

            UnmountDIT();

            Thread.Sleep(5000);

        }

        static bool MountDIT()
        {
            // mount the DIT from the IFM using dsmain.exe and read all the AD information we need


            string ditPath = @"C:\IFM\Active Directory\ntds.dit";
            int ditLDAPPort = 389;
            int ditLDAPSPort = 636;
            int ditGCPort = 3268;
            int ditGCSPort = 3269;

            string quote = "\"";
            string args = $"-dbpath {quote}{ditPath}{quote} -ldapPort {ditLDAPPort}  -sslPort {ditLDAPSPort}  -gcPort {ditGCPort} -gcSslPort {ditGCSPort} -AllowNonAdminAccess";


            // start without showing dsamain.exe
            Process p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dsamain.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    CreateNoWindow = true
                }
            };

            p.Start();

            if (p.Id == 0)
            {
                return false;
            }

            dsaMainProcess = p.Id;

            Log("DIT Mounted");

            Thread.Sleep(10000);



            return true;
        }

        static void UnmountDIT()
        {
            Process[] allLocalProcesses = Process.GetProcesses();

            foreach (Process p in allLocalProcesses)
            {
                if (dsaMainProcess == p.Id && p.ProcessName.ToLower().Contains("dsamain"))
                {
                    Log($"DIT Unmounted");
                    p.Kill();
                }
            }
        }

        static RestoredForestInfo ReadMountedDIT()
        {

            RestoredForestInfo restoredForestInfo = new RestoredForestInfo();

            LdapConnection ldapConnection = new LdapConnection(localHost);


            #region RootDSE Information
            {
                SearchRequest searchRequest = new SearchRequest(null, "(objectClass=*)", SearchScope.Base, null);
                SearchResponse searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);

                SearchResultEntry entry = searchResponse.Entries[0];
                restoredForestInfo.dnsHostName = ditInfo.dnsHostName.ToLower();       // don't get from RootDSE as this will the machine name of the server !
                restoredForestInfo.rootDomainNamingContext = ((string)entry.Attributes["rootDomainNamingContext"][0]).ToLower();
                restoredForestInfo.configurationNamingContext = ((string)entry.Attributes["configurationNamingContext"][0]).ToLower();
                restoredForestInfo.schemaNamingContext = ((string)entry.Attributes["schemaNamingContext"][0]).ToLower();
                restoredForestInfo.defaultNamingContext = ((string)entry.Attributes["defaultNamingContext"][0]).ToLower();
                foreach (string namingContext in entry.Attributes["namingContexts"].GetValues(typeof(string)))
                {
                    restoredForestInfo.namingContexts.Add(namingContext.ToLower());
                }

                restoredForestInfo.rootDomainDNS = restoredForestInfo.rootDomainNamingContext.ContextToDNS().ToLower();
            }
            #endregion

            #region Domain Controller information from CN=Sites....
            // get all valid domain controllers in forest and information from each domain controller
            {
                SearchRequest searchRequest = new SearchRequest($"CN=Sites,{restoredForestInfo.configurationNamingContext}", "(&(objectClass=server)(serverReference=*)(dnsHostName=*))", SearchScope.Subtree, null);
                SearchResponse searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);

                foreach (SearchResultEntry entry in searchResponse.Entries)
                {

                    DomainControllerInfo domainController = new DomainControllerInfo();

                    domainController.distinguishedName = entry.DistinguishedName.ToLower();
                    domainController.dsServiceName = $"cn=ntds settings,{domainController.distinguishedName}";
                    domainController.dnsHostName = ((string)entry.Attributes["dnsHostName"][0]).ToLower();
                    domainController.domainDNS = domainController.dnsHostName.Substring(domainController.dnsHostName.IndexOf(".") + 1);
                    domainController.hostNameOnly = domainController.dnsHostName.Substring(0, domainController.dnsHostName.IndexOf("."));
                    domainController.serverReference = ((string)entry.Attributes["serverReference"][0]).ToLower();
                    domainController.domainOnly = domainController.domainDNS.Substring(0, domainController.domainDNS.IndexOf("."));



                    // calculate the site. NOTE no lower case for this
                    string[] tmp = entry.DistinguishedName.Split(',');
                    domainController.site = tmp[2].Replace("CN=", "");

                    if (!restoredForestInfo.sites.Contains(domainController.site))
                    {
                        restoredForestInfo.sites.Add(domainController.site);
                    }

                    // get info for the NTDSSettings object for this domain controller

                    SearchRequest searchRequest1 = new SearchRequest($"{domainController.dsServiceName}", "(objectClass=*)", SearchScope.Base, null);
                    SearchResponse searchResponse1 = (SearchResponse)ldapConnection.SendRequest(searchRequest1);

                    // RODC will not have an invocationId
                    if (searchResponse1.Entries[0].Attributes.Contains("invocationId"))
                    {
                        domainController.invocationId = ((byte[])searchResponse1.Entries[0].Attributes["invocationId"][0]).GuidAsString();
                    }
                    else
                    {
                        domainController.invocationId = "";
                    }


                    domainController.dsaGuid = ((byte[])searchResponse1.Entries[0].Attributes["objectGuid"][0]).GuidAsString();

                    if (searchResponse1.Entries[0].Attributes.Contains("options"))
                    {
                        string options = (string)searchResponse1.Entries[0].Attributes["options"][0];

                        if ((Int32.Parse(options) & 1) == 1)
                        {
                            domainController.isGC = true;
                        }
                        else
                        {
                            domainController.isGC = false;
                        }
                    }


                    // is this dc being restored for this recovered forest ?
                    RestoredServer s = RestoredServers.Find(x => x.dnsHostName == domainController.dnsHostName);
                    if (s == null)
                    {
                        domainController.restored = false;
                        domainController.oldIP = null;
                        domainController.newIP = null;
                    }
                    else
                    {
                        domainController.restored = true;
                        domainController.oldIP = s.oldIP;
                        domainController.newIP = s.newIP;
                    }



                    restoredForestInfo.AllDomainControllers.Add(domainController);

                    if (domainController.restored)
                    {
                        restoredForestInfo.RestoredDomainControllers.Add(domainController);
                    }
                    else
                    {
                        restoredForestInfo.NotRestoredDomainControllers.Add(domainController);
                    }



                }


            }
            #endregion


            #region Partition Information
            {
                SearchRequest searchRequest = new SearchRequest($"CN=Partitions,{restoredForestInfo.configurationNamingContext}", "(objectClass=crossRef)", SearchScope.Subtree, null);
                SearchResponse searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);


                int domainCount = 0;
                foreach (SearchResultEntry entry in searchResponse.Entries)
                {
                    PartitionInfo p = new PartitionInfo();

                    p.name = ((string)entry.Attributes["name"][0]).ToLower();
                    p.nCName = ((string)entry.Attributes["nCName"][0]).ToLower();
                    p.systemFlags = (string)entry.Attributes["systemFlags"][0];
                    p.dnsRoot = ((string)entry.Attributes["dnsRoot"][0]).ToLower();
                    if (entry.Attributes.Contains("nETBIOSName"))
                    {
                        p.nETBIOSName = ((string)entry.Attributes["nETBIOSName"][0]).ToLower();
                        domainCount++;
                    }

                    if (entry.Attributes.Contains("msDS-SDReferenceDomain"))
                    {
                        p.msDSSDReferenceDomain = ((string)entry.Attributes["msDS-SDReferenceDomain"][0]).ToLower();
                    }

                    if (entry.Attributes.Contains("msDS-NC-Replica-Locations"))
                    {
                        foreach (string replicaLocation in entry.Attributes["msDS-NC-Replica-Locations"].GetValues(typeof(string)))
                        {
                            p.msDSNCReplicaLocations.Add(replicaLocation.ToLower());
                        }
                    }

                    restoredForestInfo.partitionInfo.Add(p);
                }

                if (domainCount > 1)
                {
                    restoredForestInfo.isSingleDomainForest = false;
                }



            }
            #endregion


            #region Previous FSMO owners
            {

                SearchRequest searchRequest = new SearchRequest(restoredForestInfo.schemaNamingContext, "(objectClass=*)", SearchScope.Base, null);
                SearchResponse searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);
                restoredForestInfo.fsmoSchema = ((string)searchResponse.Entries[0].Attributes["fSMORoleOwner"][0]).ToLower();

                searchRequest = new SearchRequest($"CN=Partitions,{restoredForestInfo.configurationNamingContext}", "(objectClass=*)", SearchScope.Base, null);
                searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);
                restoredForestInfo.fsmoNaming = ((string)searchResponse.Entries[0].Attributes["fSMORoleOwner"][0]).ToLower();

                searchRequest = new SearchRequest($"{restoredForestInfo.defaultNamingContext}", "(objectClass=*)", SearchScope.Base, null);
                searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);
                restoredForestInfo.fsmoPDC = ((string)searchResponse.Entries[0].Attributes["fSMORoleOwner"][0]).ToLower();

                searchRequest = new SearchRequest($"CN=RID Manager$,CN=System,{restoredForestInfo.defaultNamingContext}", "(objectClass=*)", SearchScope.Base, null);
                searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);
                restoredForestInfo.fsmoRID = ((string)searchResponse.Entries[0].Attributes["fSMORoleOwner"][0]).ToLower();

                searchRequest = new SearchRequest($"CN=Infrastructure,{restoredForestInfo.defaultNamingContext}", "(objectClass=*)", SearchScope.Base, null);
                searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);
                restoredForestInfo.fsmoInfra = ((string)searchResponse.Entries[0].Attributes["fSMORoleOwner"][0]).ToLower();



            }

            #endregion



            #region Domains in forest
            {
                SearchRequest searchRequest = new SearchRequest($"cn=partitions,{restoredForestInfo.configurationNamingContext}", "(nETBIOSName=*)", SearchScope.OneLevel, null);
                SearchResponse searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);

                foreach (SearchResultEntry entry in searchResponse.Entries)
                {
                    DomainInfo d = new DomainInfo();
                    d.domainContext = ((string)entry.Attributes["nCName"][0]).ToLower();
                    d.domainDNS = ((string)entry.Attributes["dnsRoot"][0]).ToLower();
                    d.domainOnly = d.domainDNS.Substring(0, d.domainDNS.IndexOf("."));
                    restoredForestInfo.domainInfo.Add(d);
                }

            }
            #endregion


            #region tsl
            {
                SearchRequest searchRequest = new SearchRequest($"CN=Directory Service,CN=Windows NT,CN=Services,{restoredForestInfo.configurationNamingContext}", "(objectClass=*)", SearchScope.Base, null);
                SearchResponse searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);

                if (searchResponse.Entries[0].Attributes.Contains("tombstoneLifetime"))
                {
                    restoredForestInfo.tsl = Int32.Parse((string)searchResponse.Entries[0].Attributes["tombstoneLifetime"][0]);
                }
                else
                {
                    // no tsl defined, will assume 180
                    restoredForestInfo.tsl = 180;
                }

            }
            #endregion

            ldapConnection.Dispose();




            restoredForestInfo.domainDNS = restoredForestInfo.dnsHostName.Substring(restoredForestInfo.dnsHostName.IndexOf(".") + 1);
            if (restoredForestInfo.domainDNS == restoredForestInfo.rootDomainDNS) restoredForestInfo.isRootDomainController = true;
            restoredForestInfo.hostNameOnly = restoredForestInfo.dnsHostName.Substring(0, restoredForestInfo.dnsHostName.IndexOf("."));
            restoredForestInfo.DomainControllersOU = $"ou=domain controllers,{restoredForestInfo.defaultNamingContext}";                // we are assuming in standard OU=Domain Controllers !

            restoredForestInfo.netbiosName = restoredForestInfo.partitionInfo.Find(x => x.nCName == restoredForestInfo.defaultNamingContext).nETBIOSName;


            // sort the domainController Lists, as a restored dc may read the information in AD in a different order than another restored dc
            // and we need this to be the same across all domain controllers being restored (for determing which dc is fsmo master if seized)

            List<DomainControllerInfo> sortedAllDomainControllers = restoredForestInfo.AllDomainControllers.OrderBy(x => x.domainDNS).ThenBy(x => x.dnsHostName).ToList();
            restoredForestInfo.AllDomainControllers = sortedAllDomainControllers;

            List<DomainControllerInfo> sortedRestoredDomainControllers = restoredForestInfo.RestoredDomainControllers.OrderBy(x => x.domainDNS).ThenBy(x => x.dnsHostName).ToList();
            restoredForestInfo.RestoredDomainControllers = sortedRestoredDomainControllers;

            List<DomainControllerInfo> sortedNotRestoredDomainControllers = restoredForestInfo.NotRestoredDomainControllers.OrderBy(x => x.domainDNS).ThenBy(x => x.dnsHostName).ToList();
            restoredForestInfo.NotRestoredDomainControllers = sortedNotRestoredDomainControllers;





            #region New FSMO owners

            // work out if the restored forest also restored the fsmo role
            // if not, what server will be allocated the fsmo role


            DomainControllerInfo previousfsmoSchema = restoredForestInfo.RestoredDomainControllers.Find(x => x.dsServiceName == restoredForestInfo.fsmoSchema);

            if (previousfsmoSchema != null)
            {
                restoredForestInfo.seizeSchemaMaster = false;
                restoredForestInfo.SchemaMaster = previousfsmoSchema;
            }
            else
            {
                restoredForestInfo.seizeSchemaMaster = true;
            }

            DomainControllerInfo previousfsmoNaming = restoredForestInfo.RestoredDomainControllers.Find(x => x.dsServiceName == restoredForestInfo.fsmoNaming);

            if (previousfsmoNaming != null)
            {
                restoredForestInfo.seizeNamingMaster = false;
                restoredForestInfo.NamingMaster = previousfsmoNaming;
            }
            else
            {
                restoredForestInfo.seizeNamingMaster = true;
            }

            DomainControllerInfo previousfsmoPDC = restoredForestInfo.RestoredDomainControllers.Find(x => x.dsServiceName == restoredForestInfo.fsmoPDC);

            if (previousfsmoPDC != null)
            {
                restoredForestInfo.seizePDCMaster = false;
                restoredForestInfo.PDCMaster = previousfsmoPDC;
            }
            else
            {
                restoredForestInfo.seizePDCMaster = true;
            }

            DomainControllerInfo previousfsmoRID = restoredForestInfo.RestoredDomainControllers.Find(x => x.dsServiceName == restoredForestInfo.fsmoRID);

            if (previousfsmoRID != null)
            {
                restoredForestInfo.seizeRIDMaster = false;
                restoredForestInfo.RIDMaster = previousfsmoRID;
            }
            else
            {
                restoredForestInfo.seizeRIDMaster = true;
            }


            DomainControllerInfo previousfsmoInfra = restoredForestInfo.RestoredDomainControllers.Find(x => x.dsServiceName == restoredForestInfo.fsmoInfra);

            if (previousfsmoInfra != null)
            {
                restoredForestInfo.seizeInfraMaster = false;
                restoredForestInfo.InfraMaster = previousfsmoInfra;
            }
            else
            {
                restoredForestInfo.seizeInfraMaster = true;
            }


            // if we need to seize the fsmo roles, work out which one of the restored server will get the role
            // its the first one we find in the list, schema and naming will always be seized in root domain
            // the seize will happen later in processing

            if (restoredForestInfo.seizeSchemaMaster)
            {
                DomainControllerInfo domainController = restoredForestInfo.RestoredDomainControllers.Find(x => x.domainDNS == restoredForestInfo.rootDomainDNS);
                restoredForestInfo.SchemaMaster = domainController;
            }

            if (restoredForestInfo.seizeNamingMaster)
            {
                DomainControllerInfo domainController = restoredForestInfo.RestoredDomainControllers.Find(x => x.domainDNS == restoredForestInfo.rootDomainDNS);
                restoredForestInfo.NamingMaster = domainController;
            }

            if (restoredForestInfo.seizePDCMaster)
            {
                DomainControllerInfo domainController = restoredForestInfo.RestoredDomainControllers.Find(x => x.domainDNS == restoredForestInfo.domainDNS);
                restoredForestInfo.PDCMaster = domainController;
            }

            if (restoredForestInfo.seizeRIDMaster)
            {
                DomainControllerInfo domainController = restoredForestInfo.RestoredDomainControllers.Find(x => x.domainDNS == restoredForestInfo.domainDNS);
                restoredForestInfo.RIDMaster = domainController;
            }

            if (restoredForestInfo.seizeInfraMaster)
            {
                DomainControllerInfo domainController = restoredForestInfo.RestoredDomainControllers.Find(x => x.domainDNS == restoredForestInfo.domainDNS);
                restoredForestInfo.InfraMaster = domainController;
            }

            #endregion

            #region ThisDomainController

            restoredForestInfo.ThisDomainController = restoredForestInfo.RestoredDomainControllers.Find(x => x.dnsHostName == restoredForestInfo.dnsHostName);

            if (restoredForestInfo.ThisDomainController == null)
            {
                Log($"Cannot find {restoredForestInfo.dnsHostName} in servers to be restored");
                StepFailed();
            }


            if (restoredForestInfo.ThisDomainController.dsServiceName == restoredForestInfo.RIDMaster.dsServiceName)
            {
                restoredForestInfo.ThisDomainController.isRID = true;
            }

            if (restoredForestInfo.ThisDomainController.dsServiceName == restoredForestInfo.PDCMaster.dsServiceName)
            {
                restoredForestInfo.ThisDomainController.isPDC = true;
            }

            #endregion

            // save the previous forest info
            {
                System.Xml.Serialization.XmlSerializer xmlSerializer = new System.Xml.Serialization.XmlSerializer(restoredForestInfo.GetType());
                System.IO.StreamWriter myFile = new System.IO.StreamWriter(@"C:\RestoreFromIFM\restoredForestInfo.xml", false);
                xmlSerializer.Serialize(myFile, restoredForestInfo);
                myFile.Close();
            }



            return restoredForestInfo;
        }

        static void ReadSettings()
        {
            if (File.Exists(@"C:\RestoreFromIFM\settings.xml"))
            {
                System.Xml.Serialization.XmlSerializer serializer = new System.Xml.Serialization.XmlSerializer(typeof(Settings));

                using (System.IO.Stream reader = new System.IO.FileStream(@"C:\RestoreFromIFM\settings.xml", System.IO.FileMode.Open))
                {
                    settings = (Settings)serializer.Deserialize(reader);
                }


                return;
            }


        }

        static void CreateTempAdminAccount()
        {


            // only create on RID
            if (restoredForestInfo.ThisDomainController.isRID)
            {
                string DAGroup = FindDomainAdminsGroup();

                if (DAGroup == null)
                {
                    Log($"ERROR: Cannot find the domain admins group");
                    StepFailed();
                }


                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        // we'll use SDS as need to set password

                        System.DirectoryServices.DirectoryEntry de = new System.DirectoryServices.DirectoryEntry($"LDAP://{localHost}/CN=Users,{restoredForestInfo.defaultNamingContext}");

                        System.DirectoryServices.DirectoryEntry daAccount = de.Children.Add($"CN={TempDA}", "user");
                        daAccount.Properties["SamAccountName"].Value = $"{TempDA}";
                        daAccount.CommitChanges();

                        daAccount.Invoke("SetPassword", TempDAPassword);
                        daAccount.Properties["UserAccountControl"].Value = 512;
                        daAccount.CommitChanges();

                        // add the new account into domain admins
                        System.DirectoryServices.DirectoryEntry daGroup = new System.DirectoryServices.DirectoryEntry($"LDAP://{localHost}/{DAGroup}");
                        daGroup.Properties["member"].Add($"CN={TempDA},CN=Users,{restoredForestInfo.defaultNamingContext}");
                        daGroup.CommitChanges();

                        // set primarygroupID to domain admins (maybe gets removed by restricted group GPO ?)
                        daAccount.Properties["PrimaryGroupId"].Value = 512;
                        daAccount.CommitChanges();

                        string objectSid = ((byte[])daAccount.Properties["objectSid"][0]).SidAsString();

                        Log($"Created {TempDA}  {objectSid}");
                        return;
                    }
                    catch
                    {
                        Thread.Sleep(15000);
                    }

                }

                Log($"Failed to create {TempDA} after 10 attempts");

                StepFailed();

            }

        }

        static string FindDomainAdminsGroup()
        {
            LdapConnection ldapConnection = new LdapConnection(localHost);
            SearchRequest searchRequest = new SearchRequest(restoredForestInfo.defaultNamingContext, $"(objectSid={ditInfo.DomainSid}-512)", SearchScope.Subtree, null);
            SearchResponse searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);
            if (searchResponse.Entries.Count == 1)
            {
                return searchResponse.Entries[0].DistinguishedName;
            }

            return null;
        }

        static void DeleteTempAdminAccount()
        {
            // only do on RID master
            if (restoredForestInfo.ThisDomainController.isRID)
            {
                try
                {
                    LdapConnection ldapConnection = new LdapConnection(localHost);
                    DeleteRequest deleteRequest = new DeleteRequest($"CN={TempDA},CN=Users,{restoredForestInfo.defaultNamingContext}");
                    DeleteResponse deleteResponse = (DeleteResponse)ldapConnection.SendRequest(deleteRequest);
                    Log($"Deleted {TempDA}");
                }
                catch (Exception err)
                {
                    Log($"ERROR: Deleting {TempDA} {err.Message}");
                }
            }

        }

        static void WaitForRIDToComplete()
        {

            {

                SendToConsole("ConsoleStatus:WaitingForRid");
                Log($"Waiting for RID {restoredForestInfo.RIDMaster.dnsHostName} to complete tasks");



                int timeOutMins = 15;

                Stopwatch stopWatch = new Stopwatch();
                stopWatch.Start();

                while (stopWatch.Elapsed < TimeSpan.FromMinutes(timeOutMins))
                {
                    if (ridCompleted)
                    {
                        Log($"RID {restoredForestInfo.RIDMaster.dnsHostName} completed");
                        stopWatch.Stop();
                        return;
                    }

                    Thread.Sleep(100);
                }

                Log($"ERROR: RID {restoredForestInfo.RIDMaster.dnsHostName} did not complete in {timeOutMins} mins");
                StepFailed();

            }

        }

        static void PurgeTickets()
        {
            {
                Log($"Purging tickets {restoredForestInfo.dnsHostName}");

                string args = $@"-li 0x3e7 purge";                              // purge all SYSTEM tickets
                ExecuteProcess(@"c:\windows\system32\klist.exe", args);
                Thread.Sleep(5000);
            }
        }

        static void ResetComputerPassword(string dnsHostName)
        {
            // resets the computer account on this server and updates also on RID
            Log($"Resetting computer account on {dnsHostName} on {restoredForestInfo.RIDMaster.newIP}");

            //string args = $@"/resetpwd /server:{RIDMaster.newIP} /userd:{RIDMaster.domainOnly}\{TempDA} /passwordd:{TempDAPassword}";
            //ExecuteProcessWithOutput(@"c:\windows\system32\netdom.exe", args, "Netdom.txt");
            //Thread.Sleep(5000);

            int result = NetpSetComputerAccountPassword(null, restoredForestInfo.RIDMaster.newIP, $@"{restoredForestInfo.RIDMaster.domainOnly}\{TempDA}", TempDAPassword, IntPtr.Zero);

            if (result == 0)
            {
                Log($"Computer account reset successfully");
            }
            else
            {
                Log($"ERROR: Setting computer account {result}");
            }



        }

        static bool ReplicateSingleObject(string sourceDSA, string adObject, int timeOutSecs = 60, string domain = null, string username = null, string password = null)
        {

            Exception lastError = null;

            LdapConnection ldapConnection = null;

            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            while (stopWatch.Elapsed < TimeSpan.FromSeconds(timeOutSecs))
            {
                try
                {
                    ldapConnection = new LdapConnection(restoredForestInfo.dnsHostName);

                    if (domain != null)
                    {
                        ldapConnection.Credential = new NetworkCredential(username, password, domain);
                    }


                    ModifyRequest modifyRequest = new ModifyRequest();
                    modifyRequest.DistinguishedName = null;

                    DirectoryAttributeModification mod = new DirectoryAttributeModification();
                    mod.Name = "replicateSingleObject";
                    mod.Add($"{sourceDSA}:{adObject}");
                    mod.Operation = DirectoryAttributeOperation.Replace;
                    modifyRequest.Modifications.Add(mod);

                    ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest, new TimeSpan(0, 0, 30));
                    Log($"Replicated {adObject} from {sourceDSA}");
                    return true;
                }
                catch (Exception err)
                {
                    //Log($"{sourceDSA}:{adObject}  {err.Message}");

                    lastError = err;
                    Thread.Sleep(5000);
                }
                finally
                {
                    ldapConnection.Dispose();
                }

            }

            Log($"ERROR: Replicating {adObject} from {sourceDSA}  {lastError.Message}");
            return false;
        }

        static void CheckIFM()
        {
            // check IFM matches one of the servers to be restored

            {
                RestoredServer s = RestoredServers.Find(x => x.newIP == localIPAddress);

                if (s == null)
                {
                    Log($"IFM does not match one of the servers to be restored");
                    StepFailed();
                }


                // found a server to be restored, does the hostname match the IFM
                if (s.dnsHostName != ditInfo.dnsHostName)
                {
                    Log($"IFM does not match one of the servers to be restored  server={s.dnsHostName} DIT={ditInfo.dnsHostName}");
                    StepFailed();
                }

            }



            // check we have at least one domain controller to restore per domain in forest in the server list user supplied

            foreach (DomainInfo domain in restoredForestInfo.domainInfo)
            {
                RestoredServer s = RestoredServers.Find(x => x.domainDNS == domain.domainDNS);

                if (s == null)
                {
                    Log($"ERROR: Could not find a server to restore for domain {domain.domainDNS}, at least one server is required for each domain in the forest");
                    StepFailed();
                }
            }



            if (ditInfo.isRODC)
            {
                Log($"ERROR: {ditInfo.dnsHostName} is a RODC, this cannot be restored");
                StepFailed();
            }




            // make sure this is last check !

            // check the OS version of server matches the DIT version being restored
            // we don't care about edition - standard, enterprise, datacenter

            string serverOS = GetOSVersion(localHost);

            if (serverOS == "")
            {
                Log("WARN: Could not determine OS version..continuing anyway");
                return;
            }

            if (serverOS.Contains("2012 R2") && ditInfo.OSName.Contains("2012 R2")) return;
            if (serverOS.Contains("2016") && ditInfo.OSName.Contains("2016")) return;
            if (serverOS.Contains("2019") && ditInfo.OSName.Contains("2019")) return;
            if (serverOS.Contains("2022") && ditInfo.OSName.Contains("2022")) return;
            if (serverOS.Contains("2025") && ditInfo.OSName.Contains("2025")) return;

            Log($"ERROR: OS version is {serverOS}  DIT is from {ditInfo.OSName}");
            StepFailed();

        }

        static void StepFailed(string s = "")
        {
            SetNextStep("Failed");

            if (s != "")
            {
                SendToConsole(s);
            }
            else
            {
                SendToConsole("ConsoleStatus:Failed");
            }
            Log($"Failed !! {s.Replace("ConsoleStatus:", "")}");
            StopService("_RestoreFromIFM");
            Environment.Exit(1);
            return;
        }

        static bool ListenForUDP()
        {
            if (listening) return true;

            try
            {
                serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                serverSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
                serverSocket.Bind(new IPEndPoint(IPAddress.Any, settings.udpPort));
                EndPoint newClientEP = new IPEndPoint(IPAddress.Any, 0);
                serverSocket.BeginReceiveFrom(byteData, 0, byteData.Length, SocketFlags.None, ref newClientEP, ProcessConsoleRequest, newClientEP);
                listening = true;

                return true;
            }
            catch
            {
                Log($"Could not gain exclusive access to port {settings.udpPort}");
                StepFailed();
            }

            return false;
        }

        static void ProcessConsoleRequest(IAsyncResult iar)
        {
            IPEndPoint ipe = null;

            try
            {
                EndPoint clientEP = new IPEndPoint(IPAddress.Any, 0);
                int dataLen = 0;
                byte[] data = null;
                try
                {
                    dataLen = serverSocket.EndReceiveFrom(iar, ref clientEP);
                    data = new byte[dataLen];
                    Array.Copy(byteData, data, dataLen);


                    // so we can get IP address of client that sent data
                    ipe = (IPEndPoint)clientEP;
                }
                catch
                {

                }
                finally
                {
                    EndPoint newClientEP = new IPEndPoint(IPAddress.Any, 0);

                    serverSocket.BeginReceiveFrom(byteData, 0, byteData.Length, SocketFlags.None, ref newClientEP, ProcessConsoleRequest, newClientEP);
                }


                string clientIP = ipe.Address.ToString();
                string RequestFromConsole = System.Text.Encoding.UTF8.GetString(data, 0, data.Length);

                if (RequestFromConsole.Contains("InitiateFinalSteps"))
                {
                    initiateFinalSteps = true;
                }

                if (RequestFromConsole.Contains("RID Completed"))
                {

                    //using (System.IO.StreamWriter myFileHandle = new System.IO.StreamWriter($@"C:\RestoreFromIFM\RidCompleted.txt",false))
                    //{
                    //    myFileHandle.WriteLine("RID Completed");
                    //}

                    ridCompleted = true;
                }



                if (RequestFromConsole.Contains("resumeprocessing"))
                {
                    ResumeProcessing();
                }

                if (RequestFromConsole == "Cleanup")
                {
                    cleanup = true;
                }

                if (RequestFromConsole == "Abort")
                {
                    AbortRestore();
                }

            }
            catch (ObjectDisposedException)
            {
            }
        }

        static void PauseProcessing(string step = "")
        {
            Log($"Paused {step}");
            _paused = true;

            while (_paused)
            {
                Thread.Sleep(10);
            }
        }

        static void ResumeProcessing()
        {
            Log("Resuming");
            _paused = false;
        }

        static void AbortRestore()
        {
            Log("Aborting..");
            Thread.Sleep(200);
            StopService("_RestoreFromIFM");
            DisableService("_RestoreFromIFM");
            Environment.Exit(1);

        }

        static void WaitUntilReplicationCompleted()
        {

            //TODO: put in a timer 

            try
            {
                bool replicationCompleted = false;

                Log($"Waiting for replication to complete...");

                while (!replicationCompleted)
                {
                    System.DirectoryServices.ActiveDirectory.DirectoryContext context = new System.DirectoryServices.ActiveDirectory.DirectoryContext(System.DirectoryServices.ActiveDirectory.DirectoryContextType.DirectoryServer, restoredForestInfo.dnsHostName);
                    System.DirectoryServices.ActiveDirectory.DomainController myDC = System.DirectoryServices.ActiveDirectory.DomainController.GetDomainController(context);
                    System.DirectoryServices.ActiveDirectory.ReplicationNeighborCollection replicationNeighbours = myDC.GetAllReplicationNeighbors();

                    replicationCompleted = true;

                    Log($"Checking replication status");

                    foreach (System.DirectoryServices.ActiveDirectory.ReplicationNeighbor neighbour in replicationNeighbours)
                    {
                        if (neighbour.ConsecutiveFailureCount != 0 || neighbour.LastSyncResult != 0)
                        {
                            replicationCompleted = false;
                            Log($"{neighbour.PartitionName}  {neighbour.SourceServer} {neighbour.LastSyncMessage }");

                            // destination is ourself, source is where to replicate from
                            ExecuteProcessWithOutput(@"c:\windows\system32\repadmin.exe", $"/replicate {restoredForestInfo.dnsHostName} {neighbour.SourceServer} {neighbour.PartitionName}", "Repadmin.txt");
                            //ForceReplication2();
                        }

                    }

                    if (replicationCompleted)
                    {
                        return;
                    }

                    FlushDNSServerCache();
                    FlushDNSResolverCache();


                    myDC.Dispose();
                    Thread.Sleep(60000);

                    //ForceReplication2();

                }
            }
            catch (Exception err)
            {
                Log($"ERROR: Checking replication {err.Message}");
            }


        }

        static int FlushDNSServerCache(bool quiet = false)
        {
            ManagementScope managementScope = new ManagementScope(@"\\.\root\MicrosoftDNS");

            try
            {
                managementScope.Connect();
            }
            catch
            {
                return -1;
            }

            try
            {

                SelectQuery query = new SelectQuery("MicrosoftDNS_Cache");
                ManagementObjectSearcher mgmtSrchr = new ManagementObjectSearcher(managementScope, query);
                ManagementObjectCollection managementObjectCollection = mgmtSrchr.Get();


                foreach (ManagementObject managementObject in managementObjectCollection)
                {

                    managementObject.Scope.Options.Authentication = System.Management.AuthenticationLevel.PacketPrivacy;
                    managementObject.Scope.Options.Impersonation = ImpersonationLevel.Impersonate;
                    managementObject.Scope.Options.EnablePrivileges = true;

                    managementObject.InvokeMethod("ClearCache", null);
                }

                if (!quiet) Log($"Flushed DNS server cache");
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        static void FlushDNSResolverCache(bool quiet = false)
        {
            try
            {
                DnsFlushResolverCache();
                if (!quiet) Log($"Flushed DNS resolver cache");
            }
            catch
            {

            }
        }

        static void ReplicateDomainControllerAccounts()
        {
            // replicate the computer account for each domain controllerfrom the RID master 
            // this dc will therefore have the updated password for each domain controller in its domain


            foreach (DomainControllerInfo domainController in restoredForestInfo.RestoredDomainControllers)
            {
                if (domainController.domainDNS != restoredForestInfo.domainDNS) continue;

                // no point in replicating to itself
                if (domainController.dnsHostName == restoredForestInfo.dnsHostName) continue;

                // RID master does not need to replicate as it has the latest password of all domain controllers in domain
                if (domainController.dnsHostName == restoredForestInfo.RIDMaster.dnsHostName) continue;

                ReplicateSingleObject($"{restoredForestInfo.RIDMaster.dsServiceName}", domainController.serverReference);

            }

        }

        static void ReplicateFSMORoles()
        {

            //PDC
            ReplicateSingleObject($"{restoredForestInfo.PDCMaster.dsServiceName}", restoredForestInfo.defaultNamingContext);

            //RID
            ReplicateSingleObject($"{restoredForestInfo.RIDMaster.dsServiceName}", $"CN=RID Manager$,CN=System,{restoredForestInfo.defaultNamingContext}");

            //Infra
            ReplicateSingleObject($"{restoredForestInfo.InfraMaster.dsServiceName}", $"CN=Infrastructure,{restoredForestInfo.defaultNamingContext}");

            // Schema
            ReplicateSingleObject($"{restoredForestInfo.SchemaMaster.dsServiceName}", restoredForestInfo.schemaNamingContext);


            // Naming
            ReplicateSingleObject($"{restoredForestInfo.NamingMaster.dsServiceName}", $"CN=Partitions,{restoredForestInfo.configurationNamingContext}");
        }

        static void RaiseRIDPool()
        {
            LdapConnection ldapConnection = new LdapConnection(localHost);

            SearchRequest searchRequest = new SearchRequest($"CN=RID Manager$,CN=System,{restoredForestInfo.defaultNamingContext}", "(objectClass=*)", SearchScope.Base, null);
            SearchResponse searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);


            Int64 rIDAvailablePool = Int64.Parse((string)searchResponse.Entries[0].Attributes["rIDAvailablePool"][0]);

            //convert to bytes
            byte[] byteData = BitConverter.GetBytes(rIDAvailablePool);

            uint allocatedRids = BitConverter.ToUInt32(byteData, 0);
            uint maxRids = BitConverter.ToUInt32(byteData, 4);

            Log($"Max rid pool size {maxRids}   Allocated rids {allocatedRids}");

            Int64 newrIDAvailablePool = rIDAvailablePool + 100000;

            try
            {

                ModifyRequest modifyRequest = new ModifyRequest($"CN=RID Manager$,CN=System,{restoredForestInfo.defaultNamingContext}");

                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                mod.Name = "rIDAvailablePool";
                mod.Operation = DirectoryAttributeOperation.Replace;
                mod.Add(newrIDAvailablePool.ToString());
                modifyRequest.Modifications.Add(mod);

                ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);

                Log($"Increased rIDAvailablePool by 100000 to {allocatedRids + 100000}");
            }
            catch (Exception err)
            {
                Log($"ERROR: Increasing rIDAvailablePool {err.Message}");
            }

        }

        static void InvalidateRidPool()
        {
            try
            {
                LdapConnection ldapConnection = new LdapConnection(localHost);

                SearchRequest searchRequest = new SearchRequest($"{restoredForestInfo.defaultNamingContext}", "(objectClass=*)", SearchScope.Base, null);
                SearchResponse searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);

                byte[] domainSid = (byte[])searchResponse.Entries[0].Attributes["objectSid"][0];


                ModifyRequest mRequest = new ModifyRequest(null, DirectoryAttributeOperation.Replace, "invalidateRidPool", domainSid);
                ModifyResponse mResponse = (ModifyResponse)ldapConnection.SendRequest(mRequest);

                Log("Rid pool invalidated");

            }
            catch (Exception err)
            {
                Log($"ERROR: Invalidating rid pool {err.Message}");
            }


        }

        static void AllowConsoleAccess()
        {
            Log($"Creating firewall rule to allow access from {settings.remoteIP} to UDP {settings.udpPort}");

            string psCommand = $"New-NetFirewallRule -Group 'RestoreFromIFM' -Name 'RestoreFromIFM' -DisplayName 'RestoreFromIFM' -Direction Inbound -Protocol UDP -LocalPort {settings.udpPort} -RemoteAddress '{settings.remoteIP}'";
            int processId = ExecuteProcess(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", $"-Command {psCommand}");

        }

        static void ChangeFirewallStatus(string status)
        {
            string psCommand = "";

            switch (status)
            {
                case "enabled":
                    {
                        if (!firewallDisabled) return;
                        Log($"Enabling firewall");
                        psCommand = $"Set-NetFirewallProfile -Profile Domain,Public,Private -Enabled True";
                        int processId = ExecuteProcess(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", $"-Command {psCommand}");
                        firewallDisabled = false;
                        break;
                    }

                case "disable":
                    {
                        if (firewallDisabled) return;
                        Log($"Disabling firewall");
                        psCommand = $"Set-NetFirewallProfile -Profile Domain,Public,Private -Enabled False";
                        int processId = ExecuteProcess(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", $"-Command {psCommand}");
                        firewallDisabled = true;
                        break;
                    }
            }


        }

        static void DisableGC()
        {
            // no need to disable GC if it was a single domain forest
            if (restoredForestInfo.isSingleDomainForest) return;

            // was not a GC before
            if (!restoredForestInfo.ThisDomainController.isGC) return;


            // We're going to set the options attribute to 0 regardless of what current value is
            // in fact don't want to disable inboud/outbound replication is this was enabled before

            try
            {
                LdapConnection ldapConnection = new LdapConnection(localHost);
                ModifyRequest modifyRequest = new ModifyRequest(restoredForestInfo.ThisDomainController.dsServiceName, DirectoryAttributeOperation.Replace, "options", "0");
                ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);
                Log($"Disabled Global Catalog");
            }
            catch (Exception err)
            {
                Log($"ERROR:Disabling Global Catalog {err.Message}");
            }
        }

        static void EnableGC(string dsServiceName, bool force = false)
        {

            try
            {
                LdapConnection ldapConnection = new LdapConnection(localHost);
                ModifyRequest modifyRequest = new ModifyRequest(dsServiceName, DirectoryAttributeOperation.Replace, "options", "1");

                if (force)
                {
                    DirectoryControl LDAP_SERVER_FORCE_UPDATE_OID = new DirectoryControl("1.2.840.113556.1.4.1974", null, false, true);
                    modifyRequest.Controls.Add(LDAP_SERVER_FORCE_UPDATE_OID);
                }

                ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);

            }
            catch (Exception err)
            {
                Log($"ERROR: Enabling Global Catalog on {dsServiceName} {err.Message}");
            }
        }

        static void ChangeSiteLinkOptions()
        {

            LdapConnection ldapConnection = new LdapConnection(localHost);

            SearchRequest searchRequest = new SearchRequest($"CN=IP,CN=Inter-Site Transports,CN=Sites,{restoredForestInfo.configurationNamingContext}", "(objectClass=siteLink)", SearchScope.OneLevel, null);
            SearchResponse searchResponse = (SearchResponse)ldapConnection.SendRequest(searchRequest);

            foreach (SearchResultEntry entry in searchResponse.Entries)
            {
                // save current options, so we can restore in cleanup
                if (entry.Attributes.Contains("options"))
                {
                    siteLinks.Add(entry.DistinguishedName, (string)entry.Attributes["options"][0]);
                }
                else
                {
                    siteLinks.Add(entry.DistinguishedName, "");
                }

                ModifyRequest modifyRequest = new ModifyRequest(entry.DistinguishedName);

                DirectoryAttributeModification mod = new DirectoryAttributeModification();
                mod.Name = "options";
                mod.Operation = DirectoryAttributeOperation.Replace;
                mod.Add("1");
                modifyRequest.Modifications.Add(mod);


                try
                {
                    ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);
                    Log($"Changed options on siteLink {entry.Attributes["name"][0]} to 1");
                }
                catch (Exception err)
                {
                    Log($"ERROR: Changing options on siteLink {entry.Attributes["name"][0]} to 1 {err.Message}");
                }
            }
        }

        static void RestoreSiteLinks()
        {

            LdapConnection ldapConnection = new LdapConnection(localHost);

            foreach (string siteLink in siteLinks.Keys)
            {
                try
                {

                    ModifyRequest modifyRequest = new ModifyRequest(siteLink);
                    DirectoryAttributeModification mod = new DirectoryAttributeModification();
                    mod.Name = "options";


                    if (siteLinks[siteLink] == "")
                    {
                        mod.Operation = DirectoryAttributeOperation.Delete;
                    }
                    else
                    {
                        mod.Add(siteLinks[siteLink]);
                        mod.Operation = DirectoryAttributeOperation.Replace;
                    }

                    modifyRequest.Modifications.Add(mod);

                    ModifyResponse modifyResponse = (ModifyResponse)ldapConnection.SendRequest(modifyRequest);
                    Log($"Sitelink {siteLink} restored");

                }
                catch (Exception err)
                {
                    Log($"ERROR: restoring Sitelink {siteLink} {err.Message}");
                }

            }


        }

        static void RestoreGlobalCatalogs()
        {
            if (restoredForestInfo.isSingleDomainForest) return;        // we never removed in this case so nothing to do

            // we'll only do this on the PDC of the root and let it replicate


            foreach (DomainControllerInfo domainController in restoredForestInfo.RestoredDomainControllers)
            {
                // was this a GC before
                if (domainController.isGC)
                {
                    // do 10 times to ensure it gets replicated to other dc's
                    for (int i = 0; i < 10; i++)
                    {
                        EnableGC(domainController.dsServiceName, true);
                    }

                    Log($"Enabled Global Catalog on {domainController.dsServiceName}");
                }

            }

        }

        static void CleanUp()
        {
            Log($"Performing cleanup..");

            Log($@"Setting HKLM\SYSTEM\CurrentControlSet\Services\NTDS\Parameters\Repl Perform Initial Synchronizations =1");
            RegistryKey keyToWrite = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\NTDS\Parameters");
            keyToWrite.SetValue("Repl Perform Initial Synchronizations", 1);

            // only do on pdc of root
            if (restoredForestInfo.isRootDomainController && restoredForestInfo.dnsHostName == restoredForestInfo.PDCMaster.dnsHostName)
            {
                RestoreGlobalCatalogs();
                RestoreSiteLinks();
                ForceReplicationForPartion(restoredForestInfo.configurationNamingContext, restoredForestInfo.PDCMaster.dnsHostName);
            }

            RestoreNetbiosSettings();
            DeleteTempAdminAccount();
            DisableService("_RestoreFromIFM");
            StopService("_RestoreFromIFM");


        }

        static string GetOSVersion(string server)
        {

            SelectQuery query = null;
            ManagementObjectSearcher mgmtSrchr = null;
            ManagementObjectCollection managementObjectCollection = null;
            ManagementScope managementScope = null;


            try
            {

                managementScope = new ManagementScope(new ManagementPath
                {
                    Server = server,
                    NamespacePath = @"root\cimv2"
                });


                managementScope.Connect();

            }
            catch
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
