# Pickers round 2 — cursor, Stop Emote, Unequip Cosmetics

640x360 captures from the game camera with the picker surface alpha-composited over it
(`[picker:shot] ... composited=True`), written by `picker.request`'s `capture=<tag>`. The amber
crosshair is the picker's own marker drawn into the surface at the mapped mouse position — the OS
cursor is not part of a RenderTexture and the desktop session is locked, so it cannot appear here.
Verification text and log excerpts: `../../verify/pickers.md`.

| file | state |
|---|---|
| `emote_idle.png` | emote picker, nothing playing; marker at the screen centre; `STOP EMOTE` dimmed with `NOTHING PLAYING` |
| `emote_playing.png` | `The_Griddy` playing: cell `11` marked `PLAYING`, footer `PLAYING THE_GRIDDY`, `STOP EMOTE` enabled |
| `emote_button_hover.png` | pointer on the button: hover frame (green), enabled |
| `emote_stopped.png` | after pressing it: footer `STOPPED` / `NO EMOTE`, button dimmed again |
| `cos_equipped.png` | `builtin.tophat` equipped (`EQUIPPED 1`, green `X`, live preview); `UNEQUIP COSMETICS` enabled |
| `cos_unequipped.png` | after pressing it: `EQUIPPED 0`, `SET: EMPTY`, status `UNEQUIPPED ALL`, button dimmed |
| `emote_marker_800x450.png` | same marker with the window resized to 800x450: `map screen=800x450 -> surface=640x360 scaleX=0.800`, marker at (320,180) of the capture |

![Emote picker, idle](emote_idle.png)
![Emote picker, playing](emote_playing.png)
![Emote picker, Stop Emote hovered](emote_button_hover.png)
![Emote picker, after Stop Emote](emote_stopped.png)
![Cosmetic picker, something equipped](cos_equipped.png)
![Cosmetic picker, after Unequip Cosmetics](cos_unequipped.png)
![Marker with an 800x450 window](emote_marker_800x450.png)
