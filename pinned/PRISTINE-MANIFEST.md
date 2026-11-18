# C:\AlterumPinned-pristine - pristine game archive

The game files exactly as shipped (Steam buildid 26597574), with every BepInEx / UnityDoorstop
/ gbe_fork artifact removed. This is the baseline a future Steam update is compared against;
it is NOT runnable as a mod install (no loader, no mod, genuine Steam DLL).

Generated 2026-11-14 14:13:58.

| field | value |
|---|---|
| pristine path | `C:\AlterumPinned-pristine` |
| derived from | `C:\AlterumPinned` |
| game version (Steam buildid) | `26597574` |
| unity version | `6000.3.16f1 (a56f230f6470)` |
| Alterum.exe SHA256 | `3FCCA364ACFF91980EF9C995C573A0C5AD1B0B96728A504C74F8BBFEB0B0B2D2` |
| files | 266 |
| bytes | 16396059973 |
| verified vs pin | 265 files hashed, 0 mismatches |

## What was excluded, and why

| excluded | size | why |
|---|---|---|
| `BepInEx/` | 5,447 files / 2.79 GB (was ~6.5 GB before the loader cleared `BepInEx/cache/hot` on a smoke-test launch) | the whole loader + mod tree: core runtime, plugins, hot dll, per-instance config, generated interop, unity-libs, caches, logs, screenshots. None of it ships with the game. |
| `dotnet/` | 187 files / 69 MB | BepInEx 6's bundled .NET runtime, extracted by the BepInEx installer next to the game. |
| `winhttp.dll` | 26,624 B | UnityDoorstop proxy; the game loads it as a DLL search-path hijack. This is how the loader gets in. |
| `doorstop_config.ini` | 1,637 B | UnityDoorstop config, points at `BepInEx\core\BepInEx.Unity.IL2CPP.dll`. |
| `.doorstop_version` | 5 B | UnityDoorstop version marker (`4.6.0`). |
| `changelog.txt` | 6,154 B | BepInEx 6's own changelog, extracted into the game root by its installer. |
| `steam_appid.txt` | 7 B | gbe_fork input (`4424420`). Steam does not ship this file. |
| `Alterum_Data\Plugins\x86_64\steam_api64.dll` | 11,431,336 B | the gbe_fork Steam emulator that replaced the genuine DLL. Replaced in the pristine by the genuine one (below). |
| `Alterum_Data\Plugins\x86_64\steam_api64.dll.vanilla` | 319,584 B | backup of the genuine Steam DLL. Used to restore `steam_api64.dll`, then dropped. |
| `Alterum_Data\Plugins\x86_64\steam_settings/` | 4 files / 8,285 B | gbe_fork identity + interface list (`account_name=playerA`, / `steam_interfaces.txt`). |

## Steam emulator restored to genuine

| file | bytes | sha256 |
|---|---|---|
| `steam_api64.dll` | 319584 | `670D654AA3255C5061CF0236A1CDBB2F5076CBD5AE44F613B6F4921558E83E2D` |

## Every file in the pristine archive

| bytes | sha256 | path | vs pin |
|---|---|---|---|
| 667648 | `3FCCA364ACFF91980EF9C995C573A0C5AD1B0B96728A504C74F8BBFEB0B0B2D2` | `Alterum.exe` | matches pin |
| 18 | `DEB4068A4C9EAEC651299DC76BE68BBDAD3338E5627EF5DEE93C55C95B5256D0` | `Alterum_Data\app.info` | matches pin |
| 156 | `7ACFA7DE6F57429D8407A59DDEA4002089CDA3835481B2C7F95DE8DD6F6F811E` | `Alterum_Data\boot.config` | matches pin |
| 52521124 | `3442EB1B45E5949CBEB679D2D3A0C4CD663EA34F605DD0924F837931A9458C4C` | `Alterum_Data\globalgamemanagers` | matches pin |
| 7984192 | `C7458C00DFBC60A46D63916F8A75BA8AB831BE7DE21606559BF6E6E6D7984341` | `Alterum_Data\globalgamemanagers.assets` | matches pin |
| 17359152 | `9314C72E9933BBB829D109CEFB3036AA360C3F96545FCC8FC8974B1E7B74BE92` | `Alterum_Data\globalgamemanagers.assets.resS` | matches pin |
| 26454944 | `20AA67B0A018CD3A541C34EB363E957B295F183B4AD09B7551FCB66683580716` | `Alterum_Data\il2cpp_data\Metadata\global-metadata.dat` | matches pin |
| 17 | `B18F7DBE957DEF1B2814E59A62A06831A7760D25B73ABF97AFC772B2A8814BDB` | `Alterum_Data\il2cpp_data\Resources\Coffee.SoftMaskForUGUI.R.dll-resources.dat` | matches pin |
| 17 | `B18F7DBE957DEF1B2814E59A62A06831A7760D25B73ABF97AFC772B2A8814BDB` | `Alterum_Data\il2cpp_data\Resources\Coffee.UIEffect.R.dll-resources.dat` | matches pin |
| 17 | `B18F7DBE957DEF1B2814E59A62A06831A7760D25B73ABF97AFC772B2A8814BDB` | `Alterum_Data\il2cpp_data\Resources\Coffee.UIParticle.R.dll-resources.dat` | matches pin |
| 1106 | `EC2AF79922B026438F8C021ADDDFC76DABA2BF2646393B89A84CEE812CACED1A` | `Alterum_Data\il2cpp_data\Resources\Microsoft.Bcl.TimeProvider.dll-resources.dat` | matches pin |
| 337563 | `C5BAA176A5B72CD545266340E42102D393A5E43D38C95796BC828918BB95277F` | `Alterum_Data\il2cpp_data\Resources\mscorlib.dll-resources.dat` | matches pin |
| 900 | `531DE49E73DDEA25554C9F656F54DA2A665946F3FEC57048E0CFE3B9E49B03F8` | `Alterum_Data\il2cpp_data\Resources\Newtonsoft.Json.dll-resources.dat` | matches pin |
| 1115 | `4D193560EDA545A73FA366F6A57B7F673D343791E665CE2014E865DE7F2B2926` | `Alterum_Data\il2cpp_data\Resources\ObservableCollections.dll-resources.dat` | matches pin |
| 1118 | `1E9E559A2969A9539C6526B7F9B23FE5CAFD48DC2269FE3126226FBF95F0E6E0` | `Alterum_Data\il2cpp_data\Resources\ObservableCollections.R3.dll-resources.dat` | matches pin |
| 1096 | `9EABFDE316D959A2E505B9E3471AD54239792A0127C68E1541791A20F12C1C82` | `Alterum_Data\il2cpp_data\Resources\R3.dll-resources.dat` | matches pin |
| 93718 | `98A7950EC6DE3242C2E190DFA630188DFA32511A125542B94BAA952D1C757D49` | `Alterum_Data\il2cpp_data\Resources\System.Data.dll-resources.dat` | matches pin |
| 25453 | `7C33AA707BC3FDC83991B81A97D6FC622B140762A767300F08F616C2C0A91255` | `Alterum_Data\il2cpp_data\Resources\System.Drawing.dll-resources.dat` | matches pin |
| 792 | `3CD345910C3A477F73CB4A5416DE1457B318FEE844B86527BFC0C1C3A9431986` | `Alterum_Data\il2cpp_data\Resources\System.Threading.Channels.dll-resources.dat` | matches pin |
| 2264 | `6F90499C6C0BBCA3C7D094190B786B5487BF9613742D677DF273BFD2A3577FD5` | `Alterum_Data\level0` | matches pin |
| 7648 | `27618B3ED3F69412BB36E158347F0E8F21484D332C4C8D3835E873C8643E8203` | `Alterum_Data\level1` | matches pin |
| 133152 | `56C9AEB4C6CA026EA000D292B60888F33AC33B8E2437770D0E47DB953125985D` | `Alterum_Data\level2` | matches pin |
| 8677716 | `537B1E9DE4D71CD49991ADC7A2F6290491644588D2FCB93E155CE68D1D137D8E` | `Alterum_Data\level3` | matches pin |
| 20570496 | `B2FB5F87853CCEF7FD952E221DE098BEF7016174E4E905A36133783968219666` | `Alterum_Data\level3.resS` | matches pin |
| 17995988 | `B1719CEF6FBB97FCA827F2E42FD4980C007918D025D75BAEBB5304EBE341AD5E` | `Alterum_Data\level4` | matches pin |
| 64258560 | `4510BF8CB476205BCC019E76816319243A63F5F1DA3CD6E46E08DDCBB0A3A64B` | `Alterum_Data\level4.resS` | matches pin |
| 1161456 | `50B682D54D28F1637C70E16E2F69578FAB3E2053C8D3C57B98A1C0999BF941D1` | `Alterum_Data\level5` | matches pin |
| 26385604 | `E30D635C46BD59E1DCAC064A88C9054339E27312625E2BE50FA2F2A3484EC2ED` | `Alterum_Data\level6` | matches pin |
| 56564080 | `7512173A7C5091A015AAC1BAEF135B961EB0DFD3B9CEBC99ABDF3636BEF95C8D` | `Alterum_Data\level6.resS` | matches pin |
| 119188 | `B46FD4FA5F8127937F0EE051BD65E1ECD2F8E179309B8CF0F04FCEF45F45A15F` | `Alterum_Data\level7` | matches pin |
| 81472740 | `F741DE8BF9777D2313D8A065AFBAB5355A8545BB93A6325A27B15F5A0F6A8146` | `Alterum_Data\level8` | matches pin |
| 299308672 | `F57E9E1B50D09D9ED9BDE59D48F159060196C2892E1036F8F7D64878930865D2` | `Alterum_Data\level8.resS` | matches pin |
| 128307188 | `D0F41DCE169F3F5ACCC45963976AB834F0AD2E6FCF7B9929B5CCD0F8804D9C89` | `Alterum_Data\level9` | matches pin |
| 489834720 | `BCC3F82B94E7E571398EBE5805CAA297FB7EA809EAE38D1C43769FD61264221C` | `Alterum_Data\level9.resS` | matches pin |
| 168960 | `90AA3E56F459308C4B39FF16BD87F447E8B9F3AF35F23106180D0696DCD0064F` | `Alterum_Data\Plugins\x86_64\audioplugin_phonon.dll` | matches pin |
| 3248128 | `3274C2433C0650B70FE347597B84C0CE31E9AE9273DD5CB137B6286F008781F5` | `Alterum_Data\Plugins\x86_64\fmodstudio.dll` | matches pin |
| 72192 | `E858BF4514ACA48EAB762ADCECAEDA1EDECCDDDFBE97FEB8E67795329B2F37D2` | `Alterum_Data\Plugins\x86_64\GPUUtilities.dll` | matches pin |
| 3175424 | `0E973EEF5D9A0975849C4393DDC1F3185EC627CDA624E7003A327907EB9F1BE9` | `Alterum_Data\Plugins\x86_64\lib_burst_generated.dll` | matches pin |
| 1208273 | `0492B927D1AE02CEBC0876B465760D0E352EB871FE0486CB1A4B7DA3E82FD187` | `Alterum_Data\Plugins\x86_64\libgcc_s_seh-1.dll` | matches pin |
| 19441775 | `73D3B4873FECF2686A1F6ACA077BB3335A759EA64C63A0D2312586BF57ECB9A6` | `Alterum_Data\Plugins\x86_64\libstdc++-6.dll` | matches pin |
| 25120768 | `F0EFE483A8207E11A70FC16EAA7D767A1F6D22BFE6B967CA5278E2BC7CC60E9C` | `Alterum_Data\Plugins\x86_64\libvosk.dll` | matches pin |
| 592104 | `5B60907DF42009F1BA437E7F2A6278651BE97F4993869105D8A31086138E4570` | `Alterum_Data\Plugins\x86_64\libwinpthread-1.dll` | matches pin |
| 467968 | `298E2884F0654EC5F2C1C9130250E5B6659CAE19B3633D8F16812031AB130D33` | `Alterum_Data\Plugins\x86_64\opus.dll` | matches pin |
| 52938752 | `957E8667D9D5B0E532334EC744C8897C9C09C4EC895D51A4A3FAA3C8B033A2AF` | `Alterum_Data\Plugins\x86_64\phonon.dll` | matches pin |
| 179200 | `B59A67A467E71A065E5A02892CCFFBE82DE678BC712EAB5FD9930CB60BF7CBED` | `Alterum_Data\Plugins\x86_64\phonon_fmod.dll` | matches pin |
| 816640 | `E1667E5336AE067AB1D9AA04E7BCCA9789FF5573ED4C7F6FA9305404D0485F99` | `Alterum_Data\Plugins\x86_64\resonanceaudio.dll` | matches pin |
| 319584 | `670D654AA3255C5061CF0236A1CDBB2F5076CBD5AE44F613B6F4921558E83E2D` | `Alterum_Data\Plugins\x86_64\steam_api64.dll` | vanilla (restored from .vanilla) |
| 1772032 | `44E94A5DE6345FFB2968FB1781E7EDAAB3EC3D2C0854F93BE0BB581B94C5D48C` | `Alterum_Data\Plugins\x86_64\TrueAudioNext.dll` | matches pin |
| 688693004 | `00D424098E48A35B1087AFD99F6CEEA2DA47E1D895A12769212672A1FDA008F0` | `Alterum_Data\resources.assets` | matches pin |
| 7383160784 | `616D183D5CF68884790B5E113A084202F2B2479B32B998DBEA462EAFDC501467` | `Alterum_Data\resources.assets.resS` | matches pin |
| 897292408 | `DBC332CE5CF5FDC89CA012C6B288B4B311415046421710B15F3D723498AAB077` | `Alterum_Data\resources.resource` | matches pin |
| 5923140 | `FB2492CD1AD7C38DDA8A4CE5F1A1FB57EDCFF44E3AE250B263E44BEAE3660EC4` | `Alterum_Data\Resources\unity default resources` | matches pin |
| 411576 | `C51E4FDBC24583B867EBC353755C6EFDF8AAD28F15BB1C0B99C1F377C0975BFA` | `Alterum_Data\Resources\unity_builtin_extra` | matches pin |
| 16539 | `DF56613F43CBCB77000BA75E6D3B4B662ADE225EDB81D258959E5DFE9D058C55` | `Alterum_Data\RuntimeInitializeOnLoads.json` | matches pin |
| 8833 | `DD5289591B0D8AE1E6BA8B6CEB1B9488955091D5B31282CE7C44FE58863E256E` | `Alterum_Data\ScriptingAssemblies.json` | matches pin |
| 390274824 | `CCFA0B28DC298E53F4F41AAE188C7DDD02C9F9DA4027E91CACBD9B152C839DB9` | `Alterum_Data\sharedassets0.assets` | matches pin |
| 2474585744 | `98D2AD5725DDF01F9E8F72A05C3CE79E1BA6FA0E999D2746EEA5233DD750619C` | `Alterum_Data\sharedassets0.assets.resS` | matches pin |
| 3645 | `D45947813F974D2BBE5395FB1E27FAA4C89041ECC4C451FDE7C588AECC4582F0` | `Alterum_Data\sharedassets1.assets` | matches pin |
| 9544 | `600E1000108C7260626350D7EAE900D496B861592B83CF625D85DA1835832895` | `Alterum_Data\sharedassets2.assets` | matches pin |
| 596160 | `EA2DE6DDFDB88EB47E26B784AB3AE3C637C4CC392394A39DBF2CD3D3185E56A9` | `Alterum_Data\sharedassets2.assets.resS` | matches pin |
| 897640 | `0EB0E695EAADB78A78F8CB0FF2213FC5C29AD582E4C33A41334F9B19936C9A92` | `Alterum_Data\sharedassets3.assets` | matches pin |
| 2101700 | `CAFEB13E23A33E07B56A60F59EE7498D28D1C255C517589B2C47AF081F88A2BD` | `Alterum_Data\sharedassets4.assets` | matches pin |
| 460076 | `5F53F8FCFBAEBFBAF79AA6EB1A6C6F75406ADD7AA9B926A5D36550A88C146B92` | `Alterum_Data\sharedassets5.assets` | matches pin |
| 93924333 | `D21C1578AE2EA1E897D476FEE4C6BD782DE6B69BA1AD021368F1784C1E55B871` | `Alterum_Data\sharedassets5.resource` | matches pin |
| 16762600 | `9E9A52BEE5BE14E6169C8BF959B13D0D84E25A98F0A232A6746A68144812AA1F` | `Alterum_Data\sharedassets6.assets` | matches pin |
| 1221747920 | `7F66F2AAB875C667B54495A97E719E1A9AC0E9E4EF6AC8010F6711106516E22A` | `Alterum_Data\sharedassets6.assets.resS` | matches pin |
| 7773312 | `469C2572A17591008640E99BC8FDEF06A86F96066BBAC0AC728016F316D48AA0` | `Alterum_Data\sharedassets6.resource` | matches pin |
| 4116 | `317B30F7957EE9F14500534677179BC939B6A49D95CB2A4D959E1FD12DF540D3` | `Alterum_Data\sharedassets7.assets` | matches pin |
| 58219176 | `9B538FE355B39EE3E23E2671938AA032EF829A0C993181F79E0FCE3CD3781C34` | `Alterum_Data\sharedassets8.assets` | matches pin |
| 335545120 | `EDF49917D51D4C437348386F99BC25F4F8532AC923839C9C23DF36CC23B6DAB3` | `Alterum_Data\sharedassets8.assets.resS` | matches pin |
| 15265100 | `729D3B47479AD5B92658094F4BA1B0BF5CD0D00EB786E722C1E930944204F82C` | `Alterum_Data\sharedassets9.assets` | matches pin |
| 89128960 | `565F902B3569D20F8540BCEBAAE85C93575E2162B91FD997D9F55F42D637C917` | `Alterum_Data\sharedassets9.assets.resS` | matches pin |
| 5295 | `60BA9F53458429090146F94E6D293D7BFBCCCA2C1D171B30B2F2C93C337CC780` | `Alterum_Data\StreamingAssets\aa\AddressablesLink\link.xml` | matches pin |
| 136668 | `A12467AEF9425CBA6CBB76898401829FC1A0DAD48C620319F4AF06B225913C95` | `Alterum_Data\StreamingAssets\aa\catalog.bin` | matches pin |
| 32 | `294EB93124E65B2F454903926FC334F1DAC0F37008A9166A1963C72295F8CD96` | `Alterum_Data\StreamingAssets\aa\catalog.hash` | matches pin |
| 864 | `12A13E3CF147A00CABA631874CFD1D126D8EBE95881B3D669708F4E35B20E0E0` | `Alterum_Data\StreamingAssets\aa\settings.json` | matches pin |
| 2002 | `9D413992EF02251F43CB6CF2F9495DECFE26484FD8A46991114F939CC2D14401` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\a72fbb90874632dc3a151919ac98a41f_monoscripts_6ded389f246c1c6c63581c7ab6897258.bundle` | matches pin |
| 20899693 | `5D084DAEFC0F94ABB8FF8AC4BC26367D422A56CF414A56846C81D9A6F45D2F62` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-assets-chinese(simplified)(zh-hans)_assets_all.bundle` | matches pin |
| 11232757 | `EF8ECA530C14841DE027ACEE451FBC1FB029A56E1FCAB5A253ED9EB52514031A` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-assets-japanese(ja)_assets_all.bundle` | matches pin |
| 10194770 | `91EB0101282062EF93C1F67352E18B6BE6BB713058B47DC794E867148EB40B26` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-assets-korean(ko)_assets_all.bundle` | matches pin |
| 89438255 | `52DA2A168329CF8A0E05AFC89C3B51AA2D2647395129E2312F03A287D793DF03` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-assets-russian(ru)_assets_all.bundle` | matches pin |
| 90081274 | `0DB6CE6B9ABB027B896E05D8FA6E481FA2E0BBFA60C2DF0513854D7026D52680` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-assets-shared_assets_all.bundle` | matches pin |
| 2714 | `4E3507D8A6B04A0F78040CAA950ED61669F4A89D0C616787E875562AA7ECB055` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-asset-tables-chinese(simplified)(zh-hans)_assets_all.bundle` | matches pin |
| 2757 | `AF27B187E65BEDEF37E6AAA95CEE9644AD8EDA723A2F164A3B43ACE05A253F82` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-asset-tables-english(en)_assets_all.bundle` | matches pin |
| 2756 | `4F9E3F6A096BAA1EF3A26555A4EA7CB5E307869ECF605C4A4193217F943B49E5` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-asset-tables-french(fr)_assets_all.bundle` | matches pin |
| 2754 | `6F91DDC3598367A3895D431FE02EEB7BA49C7A930938D70366B13A4F306DB28D` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-asset-tables-german(de)_assets_all.bundle` | matches pin |
| 2722 | `9BEE08652BA456D8BDD52D3FBF4D3361E701A029EAF3208FDC984C2D940FC43C` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-asset-tables-japanese(ja)_assets_all.bundle` | matches pin |
| 2721 | `AFDF045370905567B4F072A492EF7E499CF76B1C01C2048684DD659D3D478D47` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-asset-tables-korean(ko)_assets_all.bundle` | matches pin |
| 2756 | `FDE160F65DF0EC6A102464CFEB3CC288C98F57770ADFF606180BE007160CD4FB` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-asset-tables-polish(pl)_assets_all.bundle` | matches pin |
| 2763 | `6DAEF3349FD1CD83A57183613818617239512A3FB43E9AFCE296428A7B40418E` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-asset-tables-portuguese(brazil)(pt-br)_assets_all.bundle` | matches pin |
| 2750 | `B5D97970CBF6E782997F5E14F52659948486A5DCE9D43F1C873F2C176B8ECF0F` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-asset-tables-russian(ru)_assets_all.bundle` | matches pin |
| 2756 | `1E46E76976D25E0F87DD4F3EAB3D178CC22471AABE696BEBBE225B6EE6F1C02C` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-asset-tables-spanish(es)_assets_all.bundle` | matches pin |
| 2758 | `9E08C0029EA867E484AB87FCEBE5CB1594ECA28A2A89466626CECA3CF1B97E44` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-asset-tables-turkish(tr)_assets_all.bundle` | matches pin |
| 2684 | `34AC3922D37F5510E7CF6125D57A928F81F8D0AB835EEA1ECEA921F40B13FF24` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-locales_assets_all.bundle` | matches pin |
| 40173 | `A6763642D13AB72F8AAEFFC4476BB65A5F54725532FA7B0F4283ABD9D748AA42` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-chinese(simplified)(zh-hans)_assets_all.bundle` | matches pin |
| 40351 | `853CB31A8D02A1532E52101FC5AB226A690B17641A3E87A3C71B018E4C1CAFDC` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-english(en)_assets_all.bundle` | matches pin |
| 43478 | `4C981F099C5D1D88F47016C047FC9AEF7E135DD7E3D624A10B35C5F1B9CD9BF5` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-french(fr)_assets_all.bundle` | matches pin |
| 44513 | `9FFDF678F7C3B355158D99638BAEB86C8B40F081F23AFE681F2BAFA45681985C` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-german(de)_assets_all.bundle` | matches pin |
| 42415 | `2F9105E1955E300D2DC27E1B751FFED3013A5CFD0A03C864F772149E57058320` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-japanese(ja)_assets_all.bundle` | matches pin |
| 42854 | `CAEAC16CC59A2DD66119516FD7930F42ABBB4C7754B3DC7576B2EEDE8FA6072A` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-korean(ko)_assets_all.bundle` | matches pin |
| 43846 | `B93E52C88740C78B7C4F3E2D3D6DB63AFFBE5B03083B8DE8A19F4AC185C48B33` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-polish(pl)_assets_all.bundle` | matches pin |
| 42378 | `524A1BB778ED17411512D710A897C285779FE67C99C2C25AA57B945833166D7D` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-portuguese(brazil)(pt-br)_assets_all.bundle` | matches pin |
| 48118 | `9BA2DDCA978529E2D75C51C4B022775E9E29334C7159CD68F282EBA3F92E6456` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-russian(ru)_assets_all.bundle` | matches pin |
| 42414 | `4A71F878B8A16051D41647132AC4FD5B349B4DC9F01F870310CC4790522F392F` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-spanish(es)_assets_all.bundle` | matches pin |
| 42411 | `58148C18C56BA17AB5CF17191800641BF9466383C441C6AC1BABD95B4BE3E01A` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-turkish(tr)_assets_all.bundle` | matches pin |
| 66607929 | `BF9A5A91076184E58812120F6553EA9842DBC6535021BA1CA054B550B31E598C` | `Alterum_Data\StreamingAssets\aa\StandaloneWindows64\spells_assets_all_66f892708de5eac79e7e692bd1f8e9a0.bundle` | matches pin |
| 30932992 | `FD7C051685FE3D9FB851C5ED14B1E2C807AA2F482C5767E5A922AD25823BFF35` | `Alterum_Data\StreamingAssets\APVStreamingAssets\f7d28c2e14434ce49a02d85f59cabd62\04dfe5a254631ed4d9c539cfd9b3793b.bytes` | matches pin |
| 462112 | `48FC3842DD9CD59C49166B1FF5D13F7F09BBA228245D4A7AE68E5C9D35D6D533` | `Alterum_Data\StreamingAssets\APVStreamingAssets\f7d28c2e14434ce49a02d85f59cabd62\4e3e2467c4b32a44e867234bb7af9861.bytes` | matches pin |
| 7733248 | `BEB1CB32B1B747AAB135D74124518855B23D789A20DCD67E69B9E417ADFBF5F1` | `Alterum_Data\StreamingAssets\APVStreamingAssets\f7d28c2e14434ce49a02d85f59cabd62\58b815d82c230ce46810a1e59bda761e.bytes` | matches pin |
| 1933312 | `387FA56F0AB0555658F494D732D80B91B93F3424B18AC0FA15EAE42DBD2AD3AD` | `Alterum_Data\StreamingAssets\APVStreamingAssets\f7d28c2e14434ce49a02d85f59cabd62\c92b0229622f71b4d81a0db67ae00cf0.bytes` | matches pin |
| 506 | `E290EDBE81558B5FC552D2079F78B00C1B884C353A342E652CC72A7BDF2EED48` | `Alterum_Data\StreamingAssets\FMOD\Desktop\ClearVoice.bank` | matches pin |
| 33611232 | `CF9175DB4F0E4F77B6CBFB8C665256BF2BB2F4F017C16CBC31F4E81F840EF644` | `Alterum_Data\StreamingAssets\FMOD\Desktop\Game.bank` | matches pin |
| 19190 | `019E0B63E8048D4A350D996B9B1A4FDCFF76A9D1DC13691C5A61DB79BBD83ED9` | `Alterum_Data\StreamingAssets\FMOD\Desktop\Master.bank` | matches pin |
| 13332 | `94ACBA92580CBDF2039CF9347E2A8188E2B0D5753BAB2FE87833A4BAE1D4A339` | `Alterum_Data\StreamingAssets\FMOD\Desktop\Master.strings.bank` | matches pin |
| 165824 | `B9229F8D43D23965B1015AF640AC7E15F4CF07B9949F903F9FF8A41998FC64C2` | `Alterum_Data\StreamingAssets\FMOD\Desktop\Player.bank` | matches pin |
| 4597952 | `7B77907644245519B4EC3BB5E23EC1FB7F3E410119FD942B87971A84A9B8D40D` | `Alterum_Data\StreamingAssets\FMOD\Desktop\SFX.bank` | matches pin |
| 2441664 | `B3A54EDB5094C79A04DFD4409407AA568F9F3555AFB405FF7638A405F202A7AE` | `Alterum_Data\StreamingAssets\FMOD\Desktop\Trailer.bank` | matches pin |
| 1418016 | `959C4A306FB990F8B25D11A7993E8874FEDCC9750F056864675C8A08B28BE918` | `Alterum_Data\StreamingAssets\FMOD\Desktop\UI.bank` | matches pin |
| 55264 | `AC4F1596051ED6053DED5C48B6BBBE7FC4F07757A3483A4D95937093B129F7BA` | `Alterum_Data\StreamingAssets\FMOD\Desktop\Voices.bank` | matches pin |
| 15901896 | `91EDA2C04C4F599361CB92B0E5298CCDF6B3C7A1FA52BFCEFCD2C4E07AA1C131` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-cn-0.22\am\final.mdl` | matches pin |
| 161 | `5EA984DB87D7F36ED9E800E07909FB29425B42483205B58037B61B24E82BDF8A` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-cn-0.22\conf\mfcc.conf` | matches pin |
| 299 | `8EF6681D0FFCE852A1EB81DD2FB2C1C120C2D3B9ED9A405791B7EF207A54059D` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-cn-0.22\conf\model.conf` | matches pin |
| 273 | `7B59E2201ED3C860BEE3A2BAD66DAEC52B03F2B4A2F9BA67C5A05152C953A7DD` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-cn-0.22\graph\disambig_tid.int` | matches pin |
| 26082259 | `46D41A9B1723ABCCDCB354D731A5B58D87C060100D88ADA1A3E31220F52B0434` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-cn-0.22\graph\Gr.fst` | matches pin |
| 16158762 | `798AF0D9BA6F2D727747F58E1E7B2305F8618BC8EA7F6C51BEECD8A2C965B965` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-cn-0.22\graph\HCLr.fst` | matches pin |
| 7807 | `15B7D4FA1E508B1965E5C2CE0273CEEC0C097588172D37595ACDBD4E79521822` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-cn-0.22\graph\phones\word_boundary.int` | matches pin |
| 168048 | `E33276CBD548C0A03AE5812C98115940614997DFF23A6D59968AF6EAF7630FF8` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-cn-0.22\ivector\final.dubm` | matches pin |
| 9927287 | `C39D46FBFF04EFB7BAA9660AE7A728B0E2F35A4A1250B56ED3A599E26956DF86` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-cn-0.22\ivector\final.ie` | matches pin |
| 44975 | `42706D88818070EA0D0182B4D11367BA52A13B9A2BE45630F60858A422109ADA` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-cn-0.22\ivector\final.mat` | matches pin |
| 1093 | `AB2E51A7B8234916CFFD0378B7993DE4A869BF2A723183B41533D3A86F98D1F8` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-cn-0.22\ivector\global_cmvn.stats` | matches pin |
| 0 | `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-cn-0.22\ivector\online_cmvn.conf` | matches pin |
| 37 | `A1B35BAA82F5A6AEBD43DAD30186269C8CEF1879A6A1A0C15EFC4FAE5AE6B62B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-cn-0.22\ivector\splice.conf` | matches pin |
| 88 | `502531099CF1B06E18CF1696A74B901D81FEFC6EBBEF8872D0CAF0453248AA39` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-cn-0.22\README` | matches pin |
| 15749291 | `85D414100BB54C8DBA6558CE5A6DC908D2FB76B02D9A94C20ADBB36C7761811D` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-de-0.15\am\final.mdl` | matches pin |
| 114 | `F3DA9B8FDDF91FAE163A71CA663F590026968E75561E578CC5EE5437E962BE91` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-de-0.15\conf\mfcc.conf` | matches pin |
| 299 | `5C056DA2275FF4678A2B291F0E8463F1BFBE5B1F244E4B196BF8A5B4B5761641` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-de-0.15\conf\model.conf` | matches pin |
| 10349 | `B4927F667BB8EC6F638DBC69025BABE2CB93583DAB120CD0254B12EDC20CBC01` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-de-0.15\COPYING` | matches pin |
| 49 | `263FBF1021B3909566F800696D899BC66B0AF35D258C3D6A96CDDE56D0F1AAE9` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-de-0.15\graph\disambig_tid.int` | matches pin |
| 29472167 | `635CC0DD8B2D301A317613B6D1696540B665088C3EFA3E2EB4A1160E884F380F` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-de-0.15\graph\Gr.fst` | matches pin |
| 41892842 | `CD3508DCFB4548D4F57AF1B5018772FD21D16D4CBE1BE70E5208DAFC443BA14E` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-de-0.15\graph\HCLr.fst` | matches pin |
| 4916 | `94CF9C4635259BA1BFA4C561EF30CFF069EC303230BBC98A57FB93BDE9739F62` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-de-0.15\graph\phones\word_boundary.int` | matches pin |
| 168048 | `4346C68660485242E7D3880461B3A23969480ABCA1E927B41682586DA9F2E0B5` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-de-0.15\ivector\final.dubm` | matches pin |
| 8288887 | `FF40661514CC028240F3F355732D1192B98962195C42E0AE3A11E84E161D3BBD` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-de-0.15\ivector\final.ie` | matches pin |
| 33775 | `DC3F361DFEC8F9535F9EEA488AE4580B14389AEE7B7E6757418F55916CDD0A67` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-de-0.15\ivector\final.mat` | matches pin |
| 806 | `81759E93B9E526A08B8BB9FE894300A03B3E1EB0841547F97A81D4E2BDC29590` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-de-0.15\ivector\global_cmvn.stats` | matches pin |
| 0 | `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-de-0.15\ivector\online_cmvn.conf` | matches pin |
| 37 | `A1B35BAA82F5A6AEBD43DAD30186269C8CEF1879A6A1A0C15EFC4FAE5AE6B62B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-de-0.15\ivector\splice.conf` | matches pin |
| 807 | `82E93928E28FA8920283C66BA23DA3C797D9F125BE5ABD1B6C935088DED365A9` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-de-0.15\README` | matches pin |
| 15962575 | `75370A0137F9DAF8F469DEDD7DAA4513AE7A621F03240C6E512E2B50B656A7B6` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-en-us-0.15\am\final.mdl` | matches pin |
| 138 | `ED70D6EB6DCA5C45C046E624D438B61A20A2A379F7B1E790755493B6102BD4FB` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-en-us-0.15\conf\mfcc.conf` | matches pin |
| 300 | `E8159979D0DE0313FF123CE713341ABBF3C57E19263994E5DEBEB087A5DDB8FE` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-en-us-0.15\conf\model.conf` | matches pin |
| 119 | `B3610CA5E87AFCD85A886318C258CE2708A8A49962915A82CA45F28E8BC0885B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-en-us-0.15\graph\disambig_tid.int` | matches pin |
| 24013795 | `023C8B7E30704A9E37765C635C252E608A02F361235BF94ABDCF2A5225D85B20` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-en-us-0.15\graph\Gr.fst` | matches pin |
| 22416994 | `5CAAFBA3081E1646545AC6BFF0DD7A318E53DCBDC86F237909CE1D2AC1293D34` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-en-us-0.15\graph\HCLr.fst` | matches pin |
| 1927 | `83A2465C51B85AD63CA45423E3FE45C335329891B412565C0861728F5F69262D` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-en-us-0.15\graph\phones\word_boundary.int` | matches pin |
| 168048 | `8C5D7DD69D2122313BAAF19F61F35DD3FA18B70C62AC0687E311E1C46E6DACA7` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-en-us-0.15\ivector\final.dubm` | matches pin |
| 8288887 | `3F37FAF90C375B9E4740B569398B5829ED9CC07D19BE6D441F72C3B71D7EFCC6` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-en-us-0.15\ivector\final.ie` | matches pin |
| 44975 | `DDD83586DC5F928CDA8738B922C85FFE38FC789CB5F9151A712CA12F37265382` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-en-us-0.15\ivector\final.mat` | matches pin |
| 1083 | `A85F1AF667A3E229BCF18708EC24C563F62A43C3ECD9296C7A1814CC0CDD25E8` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-en-us-0.15\ivector\global_cmvn.stats` | matches pin |
| 96 | `93B9503FD7D74D762A0A16E8B2DE0056333332379FE1009818BE5A33DB823684` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-en-us-0.15\ivector\online_cmvn.conf` | matches pin |
| 37 | `A1B35BAA82F5A6AEBD43DAD30186269C8CEF1879A6A1A0C15EFC4FAE5AE6B62B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-en-us-0.15\ivector\splice.conf` | matches pin |
| 208 | `788F6F86240A149EFDE20CFEAB70D2BCA0DA58E1499CCA8BD60F55F0FF8E44C6` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-en-us-0.15\README` | matches pin |
| 16098256 | `FF9508E4DB01D2E2375DA3887973F94EF8057EE85411F4CB10A5C174CE7A4425` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-es-0.42\am\final.mdl` | matches pin |
| 163 | `FDFBDED28224482C536B1BBBA9D6241C65AEFE9348673B4A12C64C0C41BFF154` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-es-0.42\conf\mfcc.conf` | matches pin |
| 299 | `044354430C0CCB7EC61A1C24F7813359AB2B202CB48EF3352E4DE4E4118A9860` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-es-0.42\conf\model.conf` | matches pin |
| 60 | `741366F01F26BDD8730BBE6D209CF31E54B69EF17F9EB02D52E2C9997328735C` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-es-0.42\graph\disambig_tid.int` | matches pin |
| 21818215 | `0DDC8AACBC80B2E8D470060A429EB26706B2AE1D32615A499BEFD60CC86A0304` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-es-0.42\graph\Gr.fst` | matches pin |
| 12226498 | `CE42FBA42B99076F16BFE2892C3CAB0936FB7409EDBA95C545BE81C74F2F59E5` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-es-0.42\graph\HCLr.fst` | matches pin |
| 1241 | `E494AEA9300F6C8CCDD60B464B960BD0DCA27A6E4B5647848B5E280639BB9574` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-es-0.42\graph\phones\word_boundary.int` | matches pin |
| 168048 | `71B6830AE8CBEF8764CCC9A6C65943A0E80F28B32982AE4E32F2D1FF15FADF14` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-es-0.42\ivector\final.dubm` | matches pin |
| 9927287 | `601057024FC8292ED8C8BD82686F254C390868024B20C4978DFC37D82529BA05` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-es-0.42\ivector\final.ie` | matches pin |
| 44975 | `81A445F1589DE2251562879EFD0CA55D2954D937D4AE5F653AA3F11E6FDEA5EF` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-es-0.42\ivector\final.mat` | matches pin |
| 1084 | `C4A039F7F8491CCA5E49D014F37362B7EDEA4FAB407F8B9B1366477629ED7F32` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-es-0.42\ivector\global_cmvn.stats` | matches pin |
| 96 | `93B9503FD7D74D762A0A16E8B2DE0056333332379FE1009818BE5A33DB823684` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-es-0.42\ivector\online_cmvn.conf` | matches pin |
| 37 | `A1B35BAA82F5A6AEBD43DAD30186269C8CEF1879A6A1A0C15EFC4FAE5AE6B62B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-es-0.42\ivector\splice.conf` | matches pin |
| 493 | `562F159A929917F7E60EDEF2A3D0CDF8A31D39B409E3D587784EFF39E2DC3A76` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-es-0.42\README` | matches pin |
| 15978979 | `580D99752E3F6F31FB4A03403B0B2A3A78B9665937E2823668B46D9C9CE1A1A1` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-fr-0.22\am\final.mdl` | matches pin |
| 161 | `5EA984DB87D7F36ED9E800E07909FB29425B42483205B58037B61B24E82BDF8A` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-fr-0.22\conf\mfcc.conf` | matches pin |
| 299 | `E4443F961A9223D301B9B7C6FC3A68DB15DB1114AD16E173A4572776697C3721` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-fr-0.22\conf\model.conf` | matches pin |
| 456 | `F595C1ED3AAA162C4234EDDD0E48B5F75585602C470D49C49D0406A2670F974B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-fr-0.22\graph\disambig_tid.int` | matches pin |
| 24029163 | `4D5AEFC6AF664D1C47979AE3B9EB7E972C196E843FF4EEE65E4C7B0C064723CF` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-fr-0.22\graph\Gr.fst` | matches pin |
| 18331442 | `6562AEE2CFFAC5AD2E569584BC8CA22B1BAE0CE9E6E03F0F9898D9D7C267EAA5` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-fr-0.22\graph\HCLr.fst` | matches pin |
| 1780 | `6C9BBB988D49DB94A52C61974A302F5FEC187EC040F0FDFA79F5DBFC87AAA3E9` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-fr-0.22\graph\phones\word_boundary.int` | matches pin |
| 168048 | `15C6736F097E9A1FBC3811A75A666DA39CB9AA4526ACEA29D25F2A3003357ACC` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-fr-0.22\ivector\final.dubm` | matches pin |
| 9927287 | `BD52BC5AEEF1F35349C7A6FD3FB671A4A877BA04181DA30D99F2CF01D4B75DD5` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-fr-0.22\ivector\final.ie` | matches pin |
| 44975 | `61818A44AE3519708E9A31701DA4AB351F3E19D7BD3E4DDFD820FD7F121CFAD0` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-fr-0.22\ivector\final.mat` | matches pin |
| 1079 | `F155E071B892492BA87E5314E2C8B76E100D3EE7CF0C475FFE7A016E584988A7` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-fr-0.22\ivector\global_cmvn.stats` | matches pin |
| 0 | `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-fr-0.22\ivector\online_cmvn.conf` | matches pin |
| 37 | `A1B35BAA82F5A6AEBD43DAD30186269C8CEF1879A6A1A0C15EFC4FAE5AE6B62B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-fr-0.22\ivector\splice.conf` | matches pin |
| 361 | `F5C398CBC45E532E70B3672C97FD750595332E6F43228EAC447B46FBA3A830CF` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-fr-0.22\README` | matches pin |
| 16061359 | `1DF788E2C183F7FB2474C876B987E28CE40CB198DB4B1C2DCF094D9B8E3D44E2` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\am\final.mdl` | matches pin |
| 135 | `BEB4964E00108AC078B4C166B7C9E09F874934D1092BFFDCF29D43434CC83C8C` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\conf\mfcc.conf` | matches pin |
| 299 | `E4443F961A9223D301B9B7C6FC3A68DB15DB1114AD16E173A4572776697C3721` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\conf\model.conf` | matches pin |
| 754 | `0E26DE6C0B98F1DFEDFA9DB286974E03AF1287D7B2A16DAE7FBFA3C452CC3547` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\graph\disambig_tid.int` | matches pin |
| 30893295 | `4F75F879F5C96B3A5983F8AA1B2DF6E4C6F05A845D332D7F2CF410A64F095011` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\graph\Gr.fst` | matches pin |
| 37684266 | `C0BD905B506CCE18EE6A46D0A13D3C8A3310FC7B4A6B76DD99E924ED691CDDF9` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\graph\HCLr.fst` | matches pin |
| 2535 | `67873F0A10517B232DB5DE0503953CF33DE661002C19409E78ACE06118B80658` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\graph\phones.txt` | matches pin |
| 1927 | `83A2465C51B85AD63CA45423E3FE45C335329891B412565C0861728F5F69262D` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\graph\phones\word_boundary.int` | matches pin |
| 4273659 | `63812B18E7A49C500EE227E494A1525A35703E40FD40271B0939ACAB0A205A43` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\graph\words.txt` | matches pin |
| 168048 | `EBAE597E7674342D09AE0D73222260E5FBF0E3D352A3135168D1CA44189029C0` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\ivector\final.dubm` | matches pin |
| 9927287 | `71EB575F7D31721C09B1A82D58D8B5C233F73629129DF864A30DBA206BD25F66` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\ivector\final.ie` | matches pin |
| 44975 | `4DAA60EE31233F147FCC3B17A559BFF5112ADA7C14FBB20266E6748BC6BA9684` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\ivector\final.mat` | matches pin |
| 1082 | `C3F6400E40238DC6C6438D5B91EBDF8B4B6BCC8407FA4418246E19031FF1733E` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\ivector\global_cmvn.stats` | matches pin |
| 0 | `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\ivector\online_cmvn.conf` | matches pin |
| 37 | `A1B35BAA82F5A6AEBD43DAD30186269C8CEF1879A6A1A0C15EFC4FAE5AE6B62B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\ivector\splice.conf` | matches pin |
| 507 | `1EAFFEF1ACF87ED99A625EF286B7AA95F80289879391AC0EFCD969A4DDE14462` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ja-0.22\README` | matches pin |
| 15996292 | `9C557024C480F259B88D6EC01DA10082A135CBB6B54569FC057861F8E5C04A3B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pl-0.22\am\final.mdl` | matches pin |
| 161 | `5EA984DB87D7F36ED9E800E07909FB29425B42483205B58037B61B24E82BDF8A` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pl-0.22\conf\mfcc.conf` | matches pin |
| 300 | `E8159979D0DE0313FF123CE713341ABBF3C57E19263994E5DEBEB087A5DDB8FE` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pl-0.22\conf\model.conf` | matches pin |
| 49 | `5606349366AFC94FD363ADEDC31BD6F933B4D53C633489D5868C9E1E31BA143B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pl-0.22\graph\disambig_tid.int` | matches pin |
| 39605143 | `1DCB8C013954ED9AAB9070CB6C61BB79CE94CDFD5B084461BFF2F79BC0E75F5C` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pl-0.22\graph\Gr.fst` | matches pin |
| 29907218 | `DB9CDB6E454D59A696101B077E549BF5B21927E3C3D1520FD143FF35FF0F8196` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pl-0.22\graph\HCLr.fst` | matches pin |
| 1780 | `6C9BBB988D49DB94A52C61974A302F5FEC187EC040F0FDFA79F5DBFC87AAA3E9` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pl-0.22\graph\phones\word_boundary.int` | matches pin |
| 168048 | `22C8AB79F80F25D130A076F7B3003A4F4640C055EC682DACF51EFA6471102ABC` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pl-0.22\ivector\final.dubm` | matches pin |
| 9927287 | `10584CC18A6E480BEB8A342A2D4CB10D832DC4681BDE1B3991CC61F695795ED2` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pl-0.22\ivector\final.ie` | matches pin |
| 44975 | `B667EAB736B6C1CFA0210D42F472AA4002AF6EB28B6E12B06D61B67F4F45F120` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pl-0.22\ivector\final.mat` | matches pin |
| 1052 | `CFAA902D17F378C91B22493ED0C48CD54E03668E5E198D7B617CD2B1A69DBAF3` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pl-0.22\ivector\global_cmvn.stats` | matches pin |
| 0 | `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pl-0.22\ivector\online_cmvn.conf` | matches pin |
| 37 | `A1B35BAA82F5A6AEBD43DAD30186269C8CEF1879A6A1A0C15EFC4FAE5AE6B62B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pl-0.22\ivector\splice.conf` | matches pin |
| 366 | `0468DC40A5D1F07DBBF845B27A02592743F9659E8A01E5421853CD4FF1BEF4B1` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pl-0.22\README` | matches pin |
| 63 | `613B2F6D1A41036049FF3139E44DD79EB8B5B0E64D3638BBDDAB6FE3028C58BF` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pt-0.3\disambig_tid.int` | matches pin |
| 13549849 | `3E10E43EC01CF8D968BCC24C626A2EAE2BFA77863BEE82F36A554854AFA1F147` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pt-0.3\final.mdl` | matches pin |
| 16007119 | `D81023936F5557C06930802B1DB4880F56D6AC51B16ED4E5060CEBA06895442C` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pt-0.3\Gr.fst` | matches pin |
| 15510922 | `8B45BE1FA72913D61E37AC0B411177803848F4CDA7728F74139B39ACD1C0CF36` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pt-0.3\HCLr.fst` | matches pin |
| 168048 | `28DCDC395B13A2085D20F7A2E850EF7E5216BA33DED76391B9690DD60F5143DE` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pt-0.3\ivector\final.dubm` | matches pin |
| 8288887 | `9345786636C358C9539591DA71FB685AE82D8A96CE5DE533FB8AC5FDAFA965F0` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pt-0.3\ivector\final.ie` | matches pin |
| 22575 | `E310571390A3183718FCE914BB022D297777C7ED860892A1812B576A698EC420` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pt-0.3\ivector\final.mat` | matches pin |
| 550 | `83B8EE1A7B26C2A62DB0AA45A879A15D8081955532F0B5B642F8906612918820` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pt-0.3\ivector\global_cmvn.stats` | matches pin |
| 96 | `93B9503FD7D74D762A0A16E8B2DE0056333332379FE1009818BE5A33DB823684` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pt-0.3\ivector\online_cmvn.conf` | matches pin |
| 37 | `A1B35BAA82F5A6AEBD43DAD30186269C8CEF1879A6A1A0C15EFC4FAE5AE6B62B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pt-0.3\ivector\splice.conf` | matches pin |
| 161 | `8AD6E612BF219791E59298FF5841BA6CD10076155E6C8B5F8262D34BB9F486AA` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pt-0.3\mfcc.conf` | matches pin |
| 2163 | `08F0777493178AD856C7609DDFD26B972E62249BA80D3BFDDC64F25095336DE9` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pt-0.3\phones.txt` | matches pin |
| 30 | `E9C4624CB0C8C0DBFA24532B031DC6892711650436D5496AD0B937A0C57DA173` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pt-0.3\README` | matches pin |
| 2711 | `BB3240AF6BF15AF7E6702C38C3CDE738FC4C542279FD6D0727FF2BEAE81EF7E5` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-pt-0.3\word_boundary.int` | matches pin |
| 15862383 | `36D67B53DC90D96EA48097F9E17E7C5937689AD76CB8BED25091317BBAF25919` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ru-0.22\am\final.mdl` | matches pin |
| 138 | `ED70D6EB6DCA5C45C046E624D438B61A20A2A379F7B1E790755493B6102BD4FB` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ru-0.22\conf\mfcc.conf` | matches pin |
| 299 | `5C056DA2275FF4678A2B291F0E8463F1BFBE5B1F244E4B196BF8A5B4B5761641` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ru-0.22\conf\model.conf` | matches pin |
| 30 | `649BD4B396C8F438FE670ED1B0F69F278B31860021308F3F4A96E0047206EAE0` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ru-0.22\graph\disambig_tid.int` | matches pin |
| 32288259 | `189B4F67C69EB34B9D8B47B12F184665E5445667CA5264579B61C6B9EB2CEF1E` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ru-0.22\graph\Gr.fst` | matches pin |
| 32993810 | `0B096F4676F306AA3E60D2244EF1E24C5C699B8794ACF3C3946B59BCC9DCA831` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ru-0.22\graph\HCLr.fst` | matches pin |
| 2368 | `5D8D48282E2F0D56FFBB6B8726E6F9025274AF42F2950E4DF19E67668EE94A03` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ru-0.22\graph\phones\word_boundary.int` | matches pin |
| 168048 | `7DFFE22BD992D7274FBA925632BDED4756D82DFD9B962D3C703110251FDF35B8` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ru-0.22\ivector\final.dubm` | matches pin |
| 9927287 | `94F2558D00FE199AC01409281352EB16707EC075BAAF9CC0D7B53011583CFA0E` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ru-0.22\ivector\final.ie` | matches pin |
| 44975 | `956577D396B636FE359B4321F10DF5ED6A76BDFDEC366B4A2B7D3242E524F04F` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ru-0.22\ivector\final.mat` | matches pin |
| 1080 | `88294485CAAB3E6945B0AA0ECD60CC217CF443B26194262772CA97F1E6187A71` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ru-0.22\ivector\global_cmvn.stats` | matches pin |
| 96 | `93B9503FD7D74D762A0A16E8B2DE0056333332379FE1009818BE5A33DB823684` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ru-0.22\ivector\online_cmvn.conf` | matches pin |
| 37 | `A1B35BAA82F5A6AEBD43DAD30186269C8CEF1879A6A1A0C15EFC4FAE5AE6B62B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ru-0.22\ivector\splice.conf` | matches pin |
| 668 | `C9875C3F7C8C2CDB22E6F15D14F696D4967E6A2637BFD7F9AFF6016D7CA5E16B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-ru-0.22\README` | matches pin |
| 42 | `6E5C1BEE29908E2AE6692CD307E8DB1E87312ED76B4C4C2356577F4AF38204B2` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-tr-0.3\disambig_tid.int` | matches pin |
| 13304273 | `229B73463581B05A60609E5E54D3C6BB84973A37C5055218D2C587E85B2DBFD8` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-tr-0.3\final.mdl` | matches pin |
| 22638543 | `B0611E9818DCDAEA7E51F13ED6EE0ABF0A7865EA65E0A4A54B8286E200C759F0` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-tr-0.3\Gr.fst` | matches pin |
| 14343546 | `5191CC99A52B8F79A783A612FFBCBCAE6E640EE171C1C500FCCDD98F03CB17C8` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-tr-0.3\HCLr.fst` | matches pin |
| 168048 | `9E86190477F50741674B4C57269FCAFC3F6B26B7F3CC7DC1C6C172EF1297BA04` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-tr-0.3\ivector\final.dubm` | matches pin |
| 8288887 | `952CABF753B82D2D2686F473AC4F60776D65120F198A90CBBD469A39F9D0B9D5` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-tr-0.3\ivector\final.ie` | matches pin |
| 22575 | `F9527F42CFA22BACC8134177B8D77078D7C2B850C187809C5238D6BFD3665A2E` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-tr-0.3\ivector\final.mat` | matches pin |
| 553 | `41CC624FC357835E5E63FB16D694DFD610CE97F6CD09900F5F5A3AFE910FA82B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-tr-0.3\ivector\global_cmvn.stats` | matches pin |
| 96 | `93B9503FD7D74D762A0A16E8B2DE0056333332379FE1009818BE5A33DB823684` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-tr-0.3\ivector\online_cmvn.conf` | matches pin |
| 37 | `A1B35BAA82F5A6AEBD43DAD30186269C8CEF1879A6A1A0C15EFC4FAE5AE6B62B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-tr-0.3\ivector\splice.conf` | matches pin |
| 87 | `8583EF9C53600565A5FC1BF3FF25B8D27A32092B0330C615D725ED5720BE45C1` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-tr-0.3\mfcc.conf` | matches pin |
| 17 | `0EB4D5B0EA1AE21FDF8CC92D11189BC1687DDF3EA7E92F9A65DDCFF78686C24B` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-tr-0.3\README` | matches pin |
| 1437 | `BAD7F3379725B5F50B9893AC81DEF58A8F2F9D465634798D22E12F67FE2A900D` | `Alterum_Data\StreamingAssets\VoskModels\vosk-model-small-tr-0.3\word_boundary.int` | matches pin |
| 239536 | `8C381C761F4EDADA2F087F094D01079C35D1A644B055A0E5A6BFC74B5EC01BD1` | `baselib.dll` | matches pin |
| 4725304 | `625BD40005B1C3708695D5256ECF230D278E6B8EDBA5A88AF70F3D91324F3052` | `D3D12\D3D12Core.dll` | matches pin |
| 125977600 | `B7C373FAD76FE9A71223620C280EA8C50225F4A5ECA3F1ECF1DDAE53E9874011` | `GameAssembly.dll` | matches pin |
| 1621424 | `0F4C3B9F65BD1A7414C783A79BBD4AAEB661D0D52AB4F0BB843877DAC1027063` | `UnityCrashHandler64.exe` | matches pin |
| 36519336 | `BEF601AD70832ED45DA76262346D90BD8136D97F2D338DB743F02D9B2F314712` | `UnityPlayer.dll` | matches pin |

