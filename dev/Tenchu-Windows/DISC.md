# Reference disc

Hashes only. **The disc image itself is never committed** — see `.gitignore`.

| Field | Value |
|---|---|
| Title | Tenchu: Stealth Assassins (Greatest Hits) |
| Region | USA / NTSC |
| Serial | SLUS-00706 |
| Boot executable | `cdrom:\SLUS_007.06;1` |
| Track layout | Single track, `MODE2/2352`, 294,626 sectors |
| Image size | 692,960,352 bytes |

Track 1 hashes, as recorded by the ripping tool in the `.cue` itself:

| Algorithm | Value |
|---|---|
| CRC32 | `50e32c0f` |
| MD5 | `dde023c243dbd25b42a9f89d7321e104` |
| SHA1 | `a33ca500872e3471b8d096b21eff3d30a310284c` |

`SYSTEM.CNF` as read off the image:

```
BOOT=cdrom:\SLUS_007.06;1
EVENT=10
STACK=801fff00
```

Note there are no spaces around `=` and there is **no `TCB` line**. `SystemCfg.Parse` handles both:
it trims each side of the `=`, defaults `Tcb` to 4 when the key is absent, truncates the line at the
first `;`, and accepts the lowercase hex stack address.

The dump also ships `.ccd` and `.sub` sidecars (CloneCD subchannel data). The engine reads neither —
`CueBin` parses the `.cue` and follows its `FILE` line to the `.img`.
