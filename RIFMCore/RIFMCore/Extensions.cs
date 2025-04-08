using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace RIFM
{
    public static class Exensions
    {
        public static string ToDistinguishedName(this string dnsName)
        {
            // will format a dnsname to distinguishedName

            // for root.local
            // return dc=root,dc=local

            return $"dc={dnsName.Replace(".", ",dc=")}";
        }

        public static byte[] ipAddressAsBytes(this string ipAddress)
        {
            byte[] ip = { 0, 0, 0, 0 };
            string[] tmpArray = ipAddress.Split('.');

            ip[0] = byte.Parse(tmpArray[0]);
            ip[1] = byte.Parse(tmpArray[1]);
            ip[2] = byte.Parse(tmpArray[2]);
            ip[3] = byte.Parse(tmpArray[3]);

            return ip;

        }

        public static string GuidAsString(this byte[] rawObjectGuid)
        {
            Guid objectGuid = new Guid(rawObjectGuid);
            return objectGuid.ToString();
        }

        public static Guid StringAsGuid(this string guid)
        {
            return Guid.Parse(guid);
        }

        public static SecurityIdentifier StringAsSid(this string sid)
        {
            return new SecurityIdentifier(sid);
        }

        public static string HostNameFromFSMOOwner(this string fsmo)
        {
            //CN=NTDS Settings,CN=xxx,CN=Servers,CN=SITE-A,CN=Sites,CN=Configuration,DC=yyy,DC=local
            // will return xxx

            string[] tmp = fsmo.Split(',');

            return tmp[1].Replace("CN=", "").Replace("cn", "");


        }

        public static string ContextToDNS(this string context)
        {
            // will replace dc=xxx,dc=yyy to xxx.yyy
            string contextDNS = context.ToLower().Replace(",dc=", ".").Replace("dc=", "");
            return contextDNS;
        }

        public static string SidAsString(this byte[] mySid)
        {
            string asStringSid = "";
            SecurityIdentifier objectSidAsByte = new SecurityIdentifier(mySid, 0);
            asStringSid = objectSidAsByte.ToString();

            return asStringSid;
        }

    }
}
