using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RIFM
{
    public class DitInfo
    {
        public string dnsHostName;              // DNS hostname where the IFM was taken
        public string HostName;                 // just the hostname from the DNSHostName
        public string ForestName;               // dns format
        public string DomainName;               // dns format
        public string NetBIOSDomainName;
        public string DomainGuid;
        public string DomainSid;
        public string ForestMode;               // 7 etc
        public string DomainMode;
        public string ServerReference;          // CN=DCxx,OU=Domain Controllers,dc=root,dc=local
        public string ConfigurationNamingContext;
        public string dcObjectGuid;
        //public int? ReplicationEpoch;            // the replication epoch we'll set in IFM only in 4.99 build
        public string DomainNamingContext;
        public string DsaGuid;
        public string InvocationId;
        public bool IsGlobalCatalog;
        public string OSName;
        public string OSVersion;
        public uint? OSVersionMajor;
        public uint? OSVersionMinor;
        public string SchemaNamingContext;
        public string SiteName;
        public DateTime BackupExpiration;
        public bool isRODC;
    }
}
