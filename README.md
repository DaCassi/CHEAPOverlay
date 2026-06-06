# Cassi's Hyperspecific Extremely Arbitrary Portable Overlay (CHEAPOverlay)
A Sprite Based Input Overlay for Guitar Hero and Rock Band adjacent rhythm games.
Originally set out to build a sprite based input overlay for personal projects and fell into scope creep and eventually now we're here.

## What the heck does this do

CHEAPOverlay is really cool. it does a bunch of nerd stuff and basically any instrument you plug in (some exceptions) will show up as a live sprite-based input display. it also supports up to 4 different instruments at once, and voice detection.

under the hood it's polling XInput, HID, MIDI, PS2 adapters, and reading process memory from YARG and GHWTDE all at the same time and throwing it all at a WebSocket that your OBS browser source connects to. it's fine.

## How to install

1. download and unzip (or clone the repo)
2. run `install.bat` as administrator
3. follow the prompts — it'll grab .NET if you don't have it and compile the service on your machine
4. optionally install the YARG plugin for star power detection
5. add `cheapoverlay.html` to OBS as a Browser Source with **Page Transparency** enabled
6. plug in your instruments

the service starts automatically on boot. run `uninstall.bat` to remove everything.

## Multiplayer

this program does support multiple instruments. in your obs display follow the html file with ?player=n, where n is the player being tracked, starting at 0. it supports up to 4 players simultaneously.

## Sprites

the big focal point of this program is the sprites. you can set your own sprites. if you don't have the service running, you can use the html as a browser file and enter artist mode, this allows you to display all the sprites in the program. This program has over 200 sprites for every combination of frets, keys, and drum combinations you could have. 

### Keys
[![Keys Demo](https://img.youtube.com/vi/0HMWs88a6cU/0.jpg)](https://youtu.be/0HMWs88a6cU)

## STARPOWER

One of the unique features of this program is the sprite switching for activating star power in the games you play. Please see the demoes below for currently supported games.
### GHWTDE
[![GHWTDE Demo](https://img.youtube.com/vi/-89oPz7QZu8/0.jpg)](https://youtu.be/-89oPz7QZu8)
### YARG
[![YARG Demo](https://img.youtube.com/vi/nHFRGLJZVsg/0.jpg)](https://youtu.be/nHFRGLJZVsg)

## Supported inputs

- XInput (most modern controllers)
- HID (older guitars, weird adapters)
- MIDI (keyboards, drum pads)
- PS2 adapters
- Microphone / voice detection

## what's not supported

EVERY GAMEINPUT DEVICE. BASTARD API. HEADACHE PRODUCER

RB3DX. Was in the works. proved to be too difficult, may revisit.

Clone Hero. Also just difficult to read from, may revisit.

## Acknowledgements

- **[Yellow-Dog-Man](https://github.com/Yellow-Dog-Man)** — [RRNoise.NET](https://www.nuget.org/packages/YellowDogMan.RRNoise.NET), which powers the voice detection
- **[Sanjay900](https://github.com/Sanjay900)** — cool guy, go check out [Santroller](https://github.com/Sanjay900/Santroller)
- **[TheNathannator](https://github.com/TheNathannator)** — [plasticband](https://github.com/TheNathannator/plasticband), an incredibly useful reference for instrument input data

## License

see [LICENSE](LICENSE). forks are welcome but there are naming and attribution rules, give it a read.
