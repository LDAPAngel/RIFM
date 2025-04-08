using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RIFM
{
    public class PartitionInfo
    {
        public string dnsRoot;
        public string nCName;
        public string nETBIOSName = null;
        public string name;
        public string msDSSDReferenceDomain;
        public List<string> msDSNCReplicaLocations = new List<string>();
        public string systemFlags;
    }
}
