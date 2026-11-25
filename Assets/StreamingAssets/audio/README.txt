audio/ - the sounds the game plays

Every sound is built into the game (Assets/_Project/Scripts/UI/Audio/ToneBank.cs), so this folder can stay
empty and the game is still audible. A real recording dropped in here replaces the built-in one, with no
code change, no import step and no rebuild: the folder is read at runtime.

File names, all .wav, one per sound:

  click.wav  error.wav   move.wav   equip.wav  trash.wav  case.wav
  heal.wav   trade.wav   shot.wav   heartbeat.wav
  death.wav  extract.wav

  menu.wav is the music: one bed for the whole game, played on a loop. Mix it to loop seamlessly, or
  just let it run to the end and start again - whatever suits the track.

There is no sound for changing screens any more, on purpose: a click on every tab press was the one thing
the owner asked to have removed.

Only uncompressed PCM .wav is read: 8 or 16 bit, any rate from 8000 to 192000 Hz, any number of channels
(stereo and more are mixed down to mono). A file that cannot be read is named once in the console and the
built-in sound answers instead - a broken file never silences the game.

Volumes are on the SETTINGS screen and are kept in PlayerPrefs. The music level is a single global setting
(AudioDirector.MusicVolume), 40 % by default.
