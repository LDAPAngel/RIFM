using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RIFM
{
    public class RestoredServer
    {
        public string dnsHostName;      //dcxx.child1.root.local
        public string oldIP;            //IP in source forest
        public string newIP;            //IP in target i.e. restored forest
        public string domainDNS;        //child1.root.local
        public string domainOnly;       //child1
        public string hostNameOnly;     //dcxx

    }
}
