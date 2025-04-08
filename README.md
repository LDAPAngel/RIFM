# RIFM
Restore an AD forest using IFM's


Restore from IFM (RIFM) is based on the excellent work by the author of DSInternals (https://github.com/MichaelGrafnetter/DSInternals), Michael Grafnetter and IMHO is the God of active directory !

One of the powershell commands that DSInternals has is New-ADDBRestoreFromMediaScript, which generates a powershell script that will take an IFM and restore this to server thus restoring to a domain controller.
I’ve taken what Michael has done and enhanced this in RIFM

•	A console application which allows you to deploy an agent to each server to be restored in the forest. The console will also show each stage of the restore process as it progresses on each server being restored.

•	An agent which once started performs the restore without the need of any further interaction and reports the status of the restore back to the console.

•	Seizing FSMO roles if needed.

•	Metadata clean-up in active directory of all servers which are not restored.

•	RID pool increase

•	DNS clean-up, so you can restore to servers with different IP addresses than the original active directory.

•	Global catalog clean-up, so if your IFM backups from a multi domain forest were done at different times, the GC is rebuilt.

This tool can therefore be used to restore an active directory forest, providing you have at least one IFM for each domain in the forest. You can even use the tool to create an identical lab environment based on your production active directory in an isolated environment.

NOTE: This tool will only restore active directory, if you had other services such as DHCP, ADCS installed on the domain controller (BTW don’t be a knobhead and install such services on a domain controller), these are not restored.
