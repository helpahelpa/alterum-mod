Add-Type -TypeDefinition @"
using System;using System.Runtime.InteropServices;using Microsoft.Win32.SafeHandles;
public class FI2 {
  [StructLayout(LayoutKind.Sequential, Pack=1)] public struct BHF {
    public uint FileAttributes; public long c,a,w; public uint vol,fsh,fsl,nlinks,fih,fil;
  }
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  static extern SafeFileHandle CreateFileW(string p,uint acc,uint share,IntPtr sec,uint disp,uint flags,IntPtr tmpl);
  [DllImport("kernel32.dll", SetLastError=true)]
  static extern bool GetFileInformationByHandle(SafeFileHandle h, out BHF info);
  public static string Id(string path, out uint links) {
    links=0;
    var h=CreateFileW(path,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
    if(h.IsInvalid) return null;
    BHF b; if(!GetFileInformationByHandle(h,out b)){h.Dispose();return null;}
    h.Dispose(); links=b.nlinks;
    return b.vol.ToString("X8")+":"+(((ulong)b.fih<<32)|b.fil).ToString("X");
  }
}
"@
$comp = @('Alterum.exe','GameAssembly.dll','UnityPlayer.dll','winhttp.dll','baselib.dll',
          'Alterum_Data\resources.assets','Alterum_Data\resources.assets.resS','Alterum_Data\globalgamemanagers')
$B='C:\AlterumB'; $P='C:\AlterumPinned'; $S='C:\Program Files (x86)\Steam\steamapps\common\Alterum'
"{0,-42} {1,-8} {2,-20} {3,-20} {4}" -f 'file','links','B id','pin id','B==pin? B==Steam?'
foreach ($f in $comp) {
  $lb=0;$lp=0;$ls=0
  $ib=[FI2]::Id((Join-Path $B $f),[ref]$lb)
  $ip=[FI2]::Id((Join-Path $P $f),[ref]$lp)
  $is=[FI2]::Id((Join-Path $S $f),[ref]$ls)
  "{0,-42} {1,-8} {2,-20} {3,-20} {4,-8} {5}" -f $f,$lb,$ib,$ip,($ib -eq $ip),($ib -eq $is)
}
"`n--- hardlink list of B\Alterum.exe ---"
fsutil hardlink list "$B\Alterum.exe"
"`n--- per-instance identity ---"
Get-Content "$B\Alterum_Data\Plugins\x86_64\steam_settings\configs.user.ini"
"`n--- restored content in B (real files, not pin links) ---"
foreach ($d in @('BepInEx\screens','BepInEx\config','BepInEx\logs','BepInEx\hot')) {
  $m = Get-ChildItem (Join-Path $B $d) -Recurse -File -Force -EA SilentlyContinue | Measure-Object Length -Sum
  "  {0,-18} {1,6} files {2,10:N1} MB" -f $d,$m.Count,($m.Sum/1MB)
}
"`n--- share of B files that are pin inodes ---"
$pin=New-Object 'System.Collections.Generic.HashSet[string]'
Get-ChildItem $P -Recurse -File -Force -EA SilentlyContinue | ForEach-Object { [void]$pin.Add($_.FullName.Substring($P.Length+1)) }
$tot=0;$same=0
Get-ChildItem $B -Recurse -File -Force -EA SilentlyContinue | ForEach-Object {
  $rel=$_.FullName.Substring($B.Length+1); $tot++
  if ($pin.Contains($rel)) {
    $lb=0;$lp=0
    $a=[FI2]::Id($_.FullName,[ref]$lb); $b2=[FI2]::Id((Join-Path $P $rel),[ref]$lp)
    if ($a -eq $b2) { $same++ }
  }
}
"  B files: $tot ; same inode as pin: $same"
