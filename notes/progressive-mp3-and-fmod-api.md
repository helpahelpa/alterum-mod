# Progressive MP3 + FMOD API surface — experiments

Host: Windows via `ssh windows` (Game never launched; nothing under `BepInEx\plugins\*.dll`, `hot\`, or the mod source touched.)
Tools: `C:\AlterumPinned\BepInEx\plugins\alterum-music\{yt-dlp,ffmpeg,ffprobe}.exe`, FMOD runtime `C:\AlterumPinned\Alterum_Data\Plugins\x86_64\fmodstudio.dll` (**FMOD Studio 2.3.0**, proved by header-version probe: only `0x00020300` makes `FMOD_System_Create` return `FMOD_OK`).
Scratch: `C:\temp\alterum\music\` — peak 96.9 MB, cleaned to 0.01 MB (scripts only). No processes left.

## A. Progressive MP3 — YES, single-pass pipe works

### Working pipeline (exact, exit 0)

```
yt-dlp.exe --no-playlist --no-warnings --no-progress --no-mtime
           --socket-timeout 15 --retries 2 --http-chunk-size 1M
           -f bestaudio[ext=m4a]/bestaudio -o - <url>
  | ffmpeg.exe -hide_banner -nostats -nostdin -i pipe:0
               -vn -c:a libmp3lame -b:a 128k -f mp3 pipe:1
  > out.mp3
```

No temp container is needed. yt-dlp writes the downloaded bytes to stdout as they arrive; ffmpeg's
mov demuxer streams the DASH/fMP4 m4a from a non-seekable pipe without complaint
(stderr empty), and the mp3 muxer flushes frames as lame produces them. Output file growth is
steady at ~46 KB per 50 ms sample (= ~50x realtime encode).

### Measured (size sampled every 50 ms; 128 kbps CBR = 16 000 B/s)

| track | duration | first mp3 bytes | 10 s of audio | 30 s | 60 s | complete |
|---|---|---|---|---|---|---|
| `HOfdboHvshg` Sintel trailer (CC-BY) | 52.3 s | **3.90 s** | **4.05 s** | 4.35 s | – | 5.06 s |
| `aqz-KE-bpKQ` Big Buck Bunny (CC-BY) | 634.7 s | **3.83 s** | **3.98 s** | 4.33 s | 4.89 s | 17.72 s |

The ~3.8 s floor is yt-dlp's YouTube extraction, not encoding. Encoding 634 s of audio took ~0.6 s
of the 17.7 s; the rest is download. Time-to-first-10-s-of-audio is **length-independent**.

Without `--http-chunk-size 1M` the same 52 s track took 5.84 s (throttled link), still progressive.

### Growing-part-file (`ffmpeg -i src.part` while yt-dlp writes) — DOES NOT WORK

`ffmpeg -i <growing file>` reads to the current EOF and exits; it never re-reads.
Measured with `--limit-rate 400K` so the file was demonstrably still growing (2.9 MB at 11.5 s,
7.1 MB at 22.5 s, 10.27 MB final): ffmpeg exited at t=7.49 s having produced **59.95 s** of a
634 s track. No "follow" behaviour in libavformat's file protocol.

### Validity / seekability of the piped mp3

```
ffprobe: format_name=mp3 duration=634.671 bit_rate=128001 size=10154873
         stream: codec_name=mp3 sample_rate=44100 channels=2
ffmpeg -ss 300 -i out.mp3 -t 1 -f null -   -> exit 0   (mid-file seek OK, CBR)
```
Header: `ID3` v2.4, tag size 127 bytes, **no `Xing`/`Info` frame** (lame cannot write the frame count
when output is a pipe). Consequence: duration is not declared; ffprobe/FMOD have to estimate or
decode. Byte-offset seeking is exact because the stream is CBR.

### Playback side: FMOD cannot stream a file that is still being written

Tested directly against the game's own `fmodstudio.dll` from a separate process (ctypes), with a
slow copier standing in for the pipeline:

| case | result |
|---|---|
| complete mp3, `createStream` + `playSound` | OK, position advances in real time (597 ms after 0.6 s) |
| mp3 truncated to 262 KB (16 s) but static | OK, plays |
| mp3 truncated to 16 KB (1 s) | `createStream` OK but no audio, ends immediately |
| writer handle already open, then `createStream` | **`FMOD_ERR_FILE_BAD` (13)** |
| FMOD stream open, then `open(file,"ab")` from the writer | **`PermissionError` (WinError 32)** |

So FMOD opens stream files with read-only sharing: neither direction works. A growing file can never
be the thing FMOD is streaming. (This is what also blocks the "`createStream` on `*.part`" and the
"reopen at EOF" ideas in their naive form.)

Workaround that does work: the *producer* keeps its own growing file, and at the moment playback must
start, the mod copies the current partial mp3 to a **fresh snapshot path** and points `createStream` at
that. Because the pipeline encodes ~25–50x realtime it is far ahead of the playhead, so one snapshot at
~10 s of buffered audio carries a 10-minute track; a reopen + `Channel.setPosition` is only needed if
the producer ever falls behind.

## B. Usable API surface (from `refs/FMODUnity.dll`, decompiled with ilspycmd 8.2)

Everything below is in the *interop* assembly the mod already compiles against
(`BepInEx\interop\FMODUnity.dll`), namespace `FMOD` / `FMOD.Studio` / `FMODUnity`.
The mod already plays synth cues this way (`scratch/verify-mod/AlterumModelSwap.Hot/ReelAudio.cs`).

### FMODUnity.RuntimeManager (all `public static`)

| member | signature |
|---|---|
| core system | `FMOD.System CoreSystem { get; }` |
| studio system | `FMOD.Studio.System StudioSystem { get; }` |
| ready? | `bool IsInitialized { get; }` |
| muted? | `bool IsMuted { get; }` |
| mute all | `void MuteAllEvents(bool muted)` / `void ApplyMuteState()` |
| bus by path | `FMOD.Studio.Bus GetBus(string path)` |
| event instance | `FMOD.Studio.EventInstance CreateInstance(EventReference eventReference)` |
| banks | `LoadBank(string, bool loadSamples=false)`, `LoadBank(TextAsset, …)`, `UnloadBank(string)`, `AnyBankLoading()`, `WaitForAllLoads()` |

There is **no** `MasterBus`, no mixer wrapper, and no low-level `createSound`/`playSound` helper:
`grep` over the whole `FMODUnity` source finds zero calls to `createSound`/`createStream`/`playSound`.
The integration is Studio-only; the core API is reached exclusively through `RuntimeManager.CoreSystem`.

### FMOD.System (low-level, `unsafe` methods, `RESULT` return)

```csharp
RESULT createSound  (string name, MODE mode, ref CREATESOUNDEXINFO exinfo, out Sound sound);
RESULT createSound  (Il2CppStructArray<byte> data, MODE mode, ref CREATESOUNDEXINFO exinfo, out Sound sound);
RESULT createSound  (IntPtr name_or_data, MODE mode, ref CREATESOUNDEXINFO exinfo, out Sound sound);
RESULT createSound  (string name, MODE mode, out Sound sound);
RESULT createStream (string name, MODE mode, ref CREATESOUNDEXINFO exinfo, out Sound sound);
RESULT createStream (Il2CppStructArray<byte> data, MODE mode, ref CREATESOUNDEXINFO exinfo, out Sound sound);
RESULT createStream (IntPtr name_or_data, MODE mode, ref CREATESOUNDEXINFO exinfo, out Sound sound);
RESULT createStream (string name, MODE mode, out Sound sound);
RESULT playSound    (Sound sound, ChannelGroup channelgroup, bool paused, out Channel channel);
RESULT getChannel   (int channelid, out Channel channel);
RESULT getMasterChannelGroup(out ChannelGroup channelgroup);
RESULT createChannelGroup(string name, out ChannelGroup channelgroup);
RESULT attachChannelGroupToPort(PORT_TYPE portType, ulong portIndex, ChannelGroup cg, bool passThru = false);
RESULT setStreamBufferSize(uint filebuffersize, TIMEUNIT filebuffersizetype);
RESULT getStreamBufferSize(out uint filebuffersize, out TIMEUNIT filebuffersizetype);
RESULT setDSPBufferSize(uint bufferlength, int numbuffers);   RESULT getDSPBufferSize(out uint, out int);
RESULT setAdvancedSettings(ref ADVANCEDSETTINGS);             RESULT getAdvancedSettings(ref ADVANCEDSETTINGS);
RESULT getSoftwareFormat(out int samplerate, out SPEAKERMODE, out int numrawspeakers);
RESULT setFileSystem(FILE_OPEN_CALLBACK, FILE_CLOSE_CALLBACK, FILE_READ_CALLBACK,
                     FILE_SEEK_CALLBACK, FILE_ASYNCREAD_CALLBACK, FILE_ASYNCCANCEL_CALLBACK, int blockalign);
RESULT attachFileSystem(FILE_OPEN_CALLBACK, FILE_CLOSE_CALLBACK, FILE_READ_CALLBACK, FILE_SEEK_CALLBACK);
RESULT setCallback(SYSTEM_CALLBACK callback, SYSTEM_CALLBACK_TYPE mask = SYSTEM_CALLBACK_TYPE.ALL);
RESULT update();   RESULT init(int maxchannels, INITFLAGS, IntPtr extradriverdata);   RESULT close();
RESULT setUserData(IntPtr); RESULT getUserData(out IntPtr);
```

`MODE` (uint flags): `CREATESTREAM=0x80`, `CREATESAMPLE=0x100`, `CREATECOMPRESSEDSAMPLE=0x200`,
`OPENUSER=0x400`, `OPENMEMORY=0x800`, `OPENRAW=0x1000`, `OPENONLY=0x2000`, `ACCURATETIME=0x4000`,
`MPEGSEARCH=0x8000`, `NONBLOCKING=0x10000`, `UNIQUE=0x20000`, `OPENMEMORY_POINT=0x10000000`,
`LOOP_OFF/NORMAL/BIDI`, `_2D/_3D`, `IGNORETAGS`, `LOWMEM`, `VIRTUAL_PLAYFROMSTART`.

`Sound`: `getOpenState(out OPENSTATE, out uint percentbuffered, out bool starving, out bool diskbusy)`,
`getLength(out uint, TIMEUNIT)`, `getFormat(out SOUND_TYPE, out SOUND_FORMAT, out int channels, out int bits)`,
`getNumSubSounds`, `getSubSound`, `readData(Il2CppStructArray<byte>)`, `seekData(uint pcm)`,
`lock/unlock`, `setMode/getMode`, `getLoopPoints/setLoopPoints`, `getMusicNumChannels`, `release()`,
`hasHandle()`. **No `Sound.setCallback`** (FMOD removed it at 2.0).

`Channel`/`ChannelGroup` (both derive `ChannelControl`): `setVolume/getVolume`, `setVolumeRamp`,
`setPitch/getPitch`, `setPaused(bool)/getPaused(out bool)`, `setMute`, `setPosition(uint, TIMEUNIT)`,
`getPosition(out uint, TIMEUNIT)`, `isPlaying(out bool)`, `setChannelGroup(ChannelGroup)`,
`getChannelGroup(out ChannelGroup)`, `getCurrentSound(out Sound)`, `setMode/getMode`, `setFrequency`,
`addDSP`, `setUserData`, `setCallback(CHANNELCONTROL_CALLBACK)`.

`OPENSTATE` = `READY, LOADING, ERROR, CONNECTING, BUFFERING, SEEKING, PLAYING, SETPOSITION, MAX`.

### CREATESOUNDEXINFO — callbacks are *declared*, but not usable

`FMOD.CREATESOUNDEXINFO` is `[StructLayout(LayoutKind.Explicit)]` with public fields at fixed offsets:

```
cbsize 0 | length 4 | fileoffset 8 | numchannels 12 | defaultfrequency 16 | format 20
decodebuffersize 24 | initialsubsound 28 | numsubsounds 32 | inclusionlist 40 ...
pcmreadcallback_internal 56 | pcmsetposcallback_internal 64 | nonblockcallback_internal 72
dlsname 80 | encryptionkey 88 | maxpolyphony 96 | userdata 104 | suggestedsoundtype 112
fileuseropen_internal 120 ... fileuserasynccancel_internal 160 | fileuserdata 168
filebuffersize 176 | channelorder 180 | ... initialseekposition 192 ... fsbguid 216
```

plus matching properties `pcmreadcallback` / `pcmsetposcallback` / `nonblockcallback` /
`fileuseropen|close|read|seek|asyncread|asynccancel`. The delegate types are **IL2CPP delegate classes**:

```csharp
public sealed class SOUND_PCMREAD_CALLBACK : Il2CppSystem.MulticastDelegate {
    public RESULT Invoke(IntPtr sound, IntPtr data, uint datalen);
    public static implicit operator SOUND_PCMREAD_CALLBACK(
        Func<IntPtr, IntPtr, uint, RESULT> P_0);   // -> DelegateSupport.ConvertDelegate
}
```

So it *compiles*, e.g. `ex.pcmreadcallback = (Func<IntPtr,IntPtr,uint,RESULT>)OnRead;`, but the two
paths that make it run are both broken here:

1. `DelegateSupport.ConvertDelegate<T>` (Il2CppInterop 2.x) builds an Il2Cpp delegate whose
   `method_ptr` is `Marshal.GetFunctionPointerForDelegate(dynamicMethod)` and registers an injected
   `Il2CppToMonoDelegateReference` class. This is the exact machinery the mod already found failing in
   this build for `AudioClip.Create`'s PCMReaderCallback: **"Method unstripping failed"** — the reverse
   P/Invoke target for a mod-supplied method does not exist in this IL2CPP build
   (`ReelAudio.cs:20`, `tracks/merged/notes/reel-safety-feedback.md:54`).
2. The property setter is IL2CPP code operating on a `ref`-marshalled struct copy, so even a
   successfully built delegate is not guaranteed to land in the struct that reaches `createStream`.

The only plausible bypass is writing the raw field directly —
`ex.pcmreadcallback_internal = Marshal.GetFunctionPointerForDelegate(myDelegate)` — which skips the
Il2Cpp delegate entirely (the official wrapper's setter just stores that pointer anyway). It is legal
C# against this metadata and would need (a) the delegate pinned forever, (b) CoreCLR reverse P/Invoke
being callable from FMOD's mixer thread, (c) no in-game evidence yet. Untested, and the sibling
managed-callback experiment already failed. **Treat user-defined stream callbacks as unavailable.**

No managed example exists in the assembly: nothing outside `CREATESOUNDEXINFO.cs`/`SOUND_*_CALLBACK.cs`
mentions `pcmreadcallback`, `OPENUSER`, `userdata` or `setFileSystem`.

### Getting the game's master mixer / music bus

```csharp
FMOD.Studio.System studio = RuntimeManager.StudioSystem;      // or RuntimeManager.CoreSystem
FMOD.Studio.Bus    master;
studio.getBus("bus:/", out master);                            // master bus
FMOD.ChannelGroup  cg;
master.lockChannelGroup();                                     // required before routing into it
master.getChannelGroup(out cg);
channel.setChannelGroup(cg);                                   // our sound now obeys bus volume/pause/mute
master.getVolume(out float busVol, out float finalVol);        // finalVol = volume*VCA*master
```
`FMOD.Studio.Bus` has `getVolume/setVolume`, `getPaused/setPaused`, `getMute/setMute`,
`stopAllEvents`, `lockChannelGroup/unlockChannelGroup`, `getChannelGroup`, `getID/getPath`.
Bus discovery without hard-coded paths: `Bank.getBusList(out Il2CppStructArray<Bus>)` (and `getEventList`,
`getVCAList`) on each entry of `studio.getBankList(...)`.
`FMOD.Studio.EventInstance.getChannelGroup(out ChannelGroup)` also exists, so if the game already plays
music through a Studio event, the mod can attach to *that* group instead.

There is no managed "music ChannelGroup" field to reuse; `RuntimeManager` exposes no channel groups.
Playing on `CoreSystem` with `default(ChannelGroup)` (what `ReelAudio` does) bypasses Studio entirely:
the sound goes to the core master channel group, so it ignores Studio bus volumes/VCA/master fader and
`RuntimeManager.MuteAllEvents`. Attaching via `bus:/`'s channel group is the fix.

## Verdict

* Progressive MP3: **yes** — `yt-dlp -o - | ffmpeg -i - -f mp3 -`, first audio at ~4 s for any length.
* Growing-file ffmpeg *and* FMOD streaming of a growing file: **no** (ffmpeg stops at EOF; FMOD's
  `createStream` fails with `FMOD_ERR_FILE_BAD` on a write-held file, and blocks writers itself).
* FMOD can play a partial mp3 snapshot (`createStream` + `playSound`), so the realistic shape is:
  pipe → growing file → snapshot copy → `createStream`, reopen only if the producer falls behind.
* User-defined stream callbacks (`pcmreadcallback`, `OPENUSER`, `setFileSystem`): declared in metadata,
  not usable in this build.
* **Best achievable time-to-first-sound here: ~4.5 s** (3.8 s extraction + ~0.3 s to 10 s of audio +
  snapshot/`createStream`), independent of track length; ~0 ms on a cache hit.
