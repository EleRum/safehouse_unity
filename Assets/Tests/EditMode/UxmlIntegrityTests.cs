using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using NUnit.Framework;
using UnityEngine;

namespace Safehouse.Tests
{
    /// <summary>
    /// Every UXML in the project has to be well-formed and carry the top bar's tabs. A file that does not parse
    /// does not fail loudly at runtime - Unity hands the screen an empty tree, and the screen's first lookup
    /// throws a null reference that looks like a code bug. This catches it in the editor instead.
    /// </summary>
    public sealed class UxmlIntegrityTests
    {
        private static readonly string[] NavTabs =
        {
            "navtab-character", "navtab-gear", "navtab-hideout", "navtab-traders",
            "navtab-scavenge", "navtab-quests", "navtab-gm", "navtab-settings",
        };

        private static IEnumerable<string> UxmlFiles()
        {
            var root = Path.Combine(Application.dataPath, "_Project");
            return Directory.GetFiles(root, "*.uxml", SearchOption.AllDirectories);
        }

        [Test]
        public void EveryUxmlParses()
        {
            var broken = new List<string>();
            foreach (var path in UxmlFiles())
            {
                try
                {
                    var document = new XmlDocument();
                    document.Load(path);
                }
                catch (XmlException error)
                {
                    broken.Add($"{Path.GetFileName(path)}: line {error.LineNumber} - {error.Message}");
                }
            }

            Assert.IsEmpty(broken, "Malformed UXML:\n" + string.Join("\n", broken));
        }

        [Test]
        public void EveryScreenCarriesTheTopBarTabs()
        {
            var missing = new List<string>();
            foreach (var path in UxmlFiles())
            {
                var name = Path.GetFileName(path);
                if (name == "GmScreen.uxml")
                {
                    // it carries the same bar; checked by EveryScreenCarriesTheTopBarTabs through the text scan
                }

                var text = File.ReadAllText(path);
                if (text.Contains("navtab-character") == false)
                {
                    continue; // not a full screen (a fragment)
                }

                foreach (var tab in NavTabs.Where(tab => !text.Contains("name=\"" + tab + "\"")))
                {
                    missing.Add($"{name} is missing {tab}");
                }
            }

            Assert.IsEmpty(missing, "Screens without the full top bar:\n" + string.Join("\n", missing));
        }

        [Test]
        public void TheGmScreenHasItsTwoColumnsAndItsList()
        {
            var path = Path.Combine(Application.dataPath, "_Project", "UI", "GmScreen.uxml");
            var text = File.ReadAllText(path);
            foreach (var id in new[] { "gm-list", "gm-form", "gm-scroll", "gm-path", "gm-status" })
            {
                StringAssert.Contains("name=\"" + id + "\"", text);
            }
        }
    }
}
