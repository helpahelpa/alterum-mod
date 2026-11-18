Add-Type -TypeDefinition @"
using System;using System.Runtime.InteropServices;using Microsoft.Win32.SafeHandles;
public class FI3 {
  [StructLayout(LayoutKind.Sequential, Pack=1)] public struct BHF { public uint a; public long c,ac,w; public uint vol,fsh,fsl,nl,fih,fil; }
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern SafeFileHandle CreateFileW(string p,uint acc,uint share,IntPtr s,uint d,uint f,IntPtr t);
  [DllImport("kernel32.dll")] static extern bool GetFileInformationByHandle(SafeFileHandle h, out BHF i);
  public static string Id(string path, out uint links) {
    links=0; var h=CreateFileW(path,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
    if(h.IsInvalid) return null; BHF b; if(!GetFileInformationByHandle(h,out b)){h.Dispose();return null;}
    h.Dispose(); links=b.nl; return b.vol.ToString("X8")+":"+(((ulong)b.fih<<32)|b.fil).ToString("X");
  }
}
"@
$B='C:\AlterumB'; $P='C:\AlterumPinned'; $S='C:\Program Files (x86)\Steam\steamapps\common\Alterum'
$pinIds=New-Object 'System.Collections.Generic.HashSet[string]'
Get-ChildItem $P -Recurse -File -Force -EA SilentlyContinue | ForEach-Object { $l=0; [void]$pinIds.Add([FI3]::Id($_.FullName,[ref]$l)) }
$tot=0;$pinShared=0;$own=0;$ownBytes=0;$pinBytes=0
$ownFiles=@()
Get-ChildItem $B -Recurse -File -Force -EA SilentlyContinue | ForEach-Object {
  $l=0; $id=[FI3]::Id($_.FullName,[ref]$l); $tot++
  if ($pinIds.Contains($id)) { $pinShared++; $pinBytes+=$_.Length } else { $own++; $ownBytes+=$_.Length; $ownFiles+=$_.FullName.Substring($B.Length+1) }
}
"B files total     : $tot"
"  via pin inode   : $pinShared  ({0:N1} MB)" -f ($pinBytes/1MB)
"  own real files  : $own  ({0:N1} MB)" -f ($ownBytes/1MB)
"  own-file dirs   :"
$ownFiles | ForEach-Object { ($_ -split '\\')[0..1] -join '\' } | Group-Object | Sort-Object Count -Descending | Select-Object -First 8 | ForEach-Object { "     {0,-34} {1}" -f $_.Name, $_.Count }
"`n--- Steam comparison (must all be False) ---"
foreach ($f in @('Alterum.exe','GameAssembly.dll','Alterum_Data\resources.assets.resS')) {
  $l1=0;$l2=0;$l3=0
  $ib=[FI3]::Id("$B\$f",[ref]$l1); $ip=[FI3]::Id("$P\$f",[ref]$l2); $is=[FI3]::Id("$S\$f",[ref]$l3)
  "  {0,-34} B==pin {1,-6} B==Steam {2}" -f $f,($ib -eq $ip),($ib -eq $is)
}
"`n--- hot files no longer pin inodes ---"
Get-ChildItem 'C:\AlterumB\BepInEx\hot' -Recurse -File -Force | ForEach-Object { $l=0; "  {0,-40} links={1} pinInode={2}" -f $_.Name,$l,($pinIds.Contains([FI3]::Id($_.FullName,[ref]$l))) }
"`n--- installs present ---"
Get-ChildItem C:\ -Directory -Force -EA SilentlyContinue | Where-Object { $_.Name -match 'Alterum' } | ForEach-Object {
  $m = Get-ChildItem $_.FullName -Recurse -File -Force -EA SilentlyContinue | Measure-Object Length -Sum
  "  {0,-26} {1,6} files {2,8:N1} GB" -f $_.FullName, $m.Count, ($m.Sum/1GB)
}
"`n--- B exe links ---"; fsutil hardlink list "$B\Alterum.exe"
"--- free ---"; $d=Get-PSDrive C; "freeGB={0:N3}" -f ($d.Free/1GB)
"--- processes ---"; Get-Process -Name Alterum -EA SilentlyContinue | ForEach-Object { "  pid=$($_.Id) $($_.Path)" }
