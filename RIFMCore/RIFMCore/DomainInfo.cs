using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RIFM
{
    public class DomainInfo
    {
        //public string domainGuid;                                       // the objectGuid of this domain, used when fixing DNS
        public string domainDNS;                                        // in dns format e.g. domain.local
        public string domainOnly;                                       // just the domain name i.e. domain
        public string domainContext;                                    // dc=domain,dc=local
    }
}
