using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Drawing;

namespace RIFMConsole
{
   public class server
    {
        public Color colour = SystemColors.Control;

        public string fqdn;
        public string hostname;                 // just the name of the server from fqdn
        public string domainDNS;                // just the domain of the server
        public string currentIP;
        public string newIP;
        public bool RestoreCompleted=false;
        public bool Completed = false;
        public bool Failed=false;

        public bool ADDSFailed = false;
        public bool PromoFailed = false;
        public bool RenameFailed = false;


        public bool RenameCompleted = false;
        public bool PromoCompleted = false;
        public bool DatabaseOperationsCompleted = false;
        public bool RIDCompleted = false;
        public bool WaitingForRid = false;

    }
}
