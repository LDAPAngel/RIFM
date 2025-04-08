using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RIFM
{
    public class RestoredForestInfo
    {
        // from RootDSE
        public string dnsHostName;
        public string domainDNS;                                        // the dns domain this dc belongs to, calculated
        public bool isSingleDomainForest = true;                        // calculated
        public bool isRootDomainController = false;                     // calculated, true if this dc is member of root domain
        public string hostNameOnly;                                     // calculated
        public string rootDomainNamingContext;                          // dc=...
        public string defaultNamingContext;                             // dc=...
        public string configurationNamingContext;                       // cn=configuration...
        public string schemaNamingContext;                              // cn=Schema....
        public string rootDomainDNS;                                    // the root domain in dns format, calculated
        public string netbiosName;                                      // netbios name of this dc's domain
        public int tsl;                                                 // tombstone lifetime 

        public List<string> namingContexts = new List<string>();


        // From CN=Sites...
        public List<DomainControllerInfo> AllDomainControllers = new List<DomainControllerInfo>();
        public List<DomainControllerInfo> RestoredDomainControllers = new List<DomainControllerInfo>();
        public List<DomainControllerInfo> NotRestoredDomainControllers = new List<DomainControllerInfo>();

        // from CN=Partitions
        public List<PartitionInfo> partitionInfo = new List<PartitionInfo>();

        public string fsmoSchema;                                       // fsmo owners in original forest the dsServiceName
        public string fsmoNaming;
        public string fsmoPDC;
        public string fsmoRID;
        public string fsmoInfra;

        public bool seizeSchemaMaster = false;
        public bool seizeNamingMaster = false;
        public bool seizePDCMaster = false;
        public bool seizeRIDMaster = false;
        public bool seizeInfraMaster = false;

        // From the GC
        public List<DomainInfo> domainInfo = new List<DomainInfo>();

        public string DomainControllersOU;

        public List<string> sites = new List<string>();                 // calculated from domain controllers


        public DomainControllerInfo ThisDomainController;


        // the new fsmo owners, maybe seized
        public DomainControllerInfo SchemaMaster;
        public DomainControllerInfo NamingMaster;
        public DomainControllerInfo PDCMaster;
        public DomainControllerInfo RIDMaster;
        public DomainControllerInfo InfraMaster;

    }
}
