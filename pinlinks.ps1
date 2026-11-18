foreach ($f in @('C:\AlterumPinned\Alterum.exe','C:\AlterumPinned\GameAssembly.dll','C:\AlterumPinned\UnityPlayer.dll','C:\AlterumPinned-pristine\Alterum.exe')) {
  "=== $f"
  fsutil hardlink list $f
  "count: " + ((fsutil hardlink list $f | Measure-Object).Count)
}
