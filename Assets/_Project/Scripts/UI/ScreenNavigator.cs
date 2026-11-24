using System;
using System.Collections.Generic;
using System.Linq;

namespace Safehouse.UI
{
    /// <summary>
    /// Which screen is showing. Each screen registers how to show and hide itself; <see cref="Go"/> shows one and
    /// hides the rest. The screens are separate UI documents in one scene, and this is all they know about each
    /// other - the top bar's tabs call <see cref="Go"/> with a screen's name and nothing else.
    /// </summary>
    public static class ScreenNavigator
    {
        private sealed class Screen
        {
            public Action Show;
            public Action Hide;
        }

        private static readonly Dictionary<string, Screen> Screens = new Dictionary<string, Screen>();

        /// <summary>The bed every screen asks for. One id means one continuous piece of music across the whole
        /// game: the only thing a screen change does is play the telegraph key over it.</summary>
        private const string MenuBed = "menu";

        /// <summary>The screen last shown with <see cref="Go"/>, or null if none has been (the scene's first screen is showing).</summary>
        public static string Current { get; private set; }

        public static void Register(string name, Action show, Action hide)
        {
            Screens[name] = new Screen { Show = show, Hide = hide };
        }

        public static void Unregister(string name)
        {
            Screens.Remove(name);
            if (Current == name)
            {
                Current = null;
            }
        }

        /// <summary>Shows the named screen and hides every other. An unknown name changes nothing.</summary>
        public static void Go(string name)
        {
            if (!Screens.ContainsKey(name))
            {
                return;
            }

            // One place for the two things every screen change should sound like: a telegraph key, and the
            // bed. There is deliberately only one bed: asking for the same id again does nothing, so changing
            // screens lets the music keep playing instead of restarting it. "raid" stays available for a
            // screen that genuinely wants its own music - call AudioDirector.Music("raid") from that screen.
            AudioDirector.Play(Sound.Telegraph);
            AudioDirector.Music(MenuBed);

            foreach (var other in Screens.Where(pair => pair.Key != name).ToList())
            {
                other.Value.Hide();
            }

            Screens[name].Show();
            Current = name;
        }
    }
}
