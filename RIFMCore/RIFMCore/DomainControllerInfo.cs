using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RIFM
{
    public class DomainControllerInfo
    {
        // from the server object in config partition
        public string dnsHostName;
        public string domainDNS;                                        // the dns domain this dc belongs to, calculated
        public string hostNameOnly;                                     // calculated
        public string domainOnly;                                       // calculated, child1
        public string serverReference;                                  // cn=dc01,ou=domain controllers,dc=acme,dc=com
        public string distinguishedName;
        public string dsServiceName;                                    // cn=ntds settings,<distinguishedName>
        public string site;                                             // calculated, case not changed to lower !


        // from the NTDSSetting for this domain controller
        public string invocationId;
        public string dsaGuid;                                          // objectGuid attribute, this is <guid>._msdcs.root.local as a CNAME to hostname of the dc
        public bool isGC = false;

        // calculated
        public bool restored = false;                                   // if the this dc was to be restored from IFM
        public string oldIP;                                            // IP in source forest
        public string newIP;                                            // IP in target i.e. restored forest

        // only set on ThisDomainController
        public bool isRID = false;
        public bool isPDC = false;

    }

}
