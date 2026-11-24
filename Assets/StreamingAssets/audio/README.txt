audio/ - the sounds the game plays

Every sound is built into the game (Assets/_Project/Scripts/UI/Audio/ToneBank.cs), so this folder can stay
empty and the game is still audible. A real recording dropped in here replaces the built-in one, with no
code change, no import step and no rebuild: the folder is read at runtime.

File names, all .wav, one per sound:

  click.wav  telegraph.wav  error.wav  move.wav  equip.wav  trash.wav  case.wav
  heal.wav   shot.wav   death.wav extract.wav
  menu.wav   raid.wav

  telegraph is the tab bar: a Morse key. menu and raid are the two music beds. They loop, so they
  should be mixed to loop seamlessly.

Only uncompressed PCM .wav is read: 8 or 16 bit, any rate from 8000 to 192000 Hz, any number of channels
(stereo and more are mixed down to mono). A file that cannot be read is named once in the console and the
built-in sound answers instead - a broken file never silences the game.

Volumes are on the SETTINGS screen and are kept in PlayerPrefs.
