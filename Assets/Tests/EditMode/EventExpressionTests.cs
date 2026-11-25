using System.Collections.Generic;
using NUnit.Framework;
using Safehouse.Core;

namespace Safehouse.Tests
{
    /// <summary>
    /// The little language a GM writes conditions in. It has to be predictable - a scene that silently does the
    /// wrong thing is worse than one that refuses to load - so every operator is pinned here.
    /// </summary>
    public sealed class EventExpressionTests
    {
        private static int Eval(string text, PythonRandom random = null, int roubles = 0,
            int party = 1, params string[] carried)
        {
            var values = new Dictionary<string, int>
            {
                ["muscle"] = 6, ["sense"] = 4, ["perception"] = 7, ["humanity"] = 5,
            };
            var context = new ExpressionContext(values, roubles, party,
                id => new List<string>(carried).Contains(id), null, random);
            return EventExpression.Evaluate(text, context);
        }

        [Test]
        public void ArithmeticAndComparisonsArePlainIntegers()
        {
            Assert.AreEqual(10, Eval("4 + 6"));
            Assert.AreEqual(2, Eval("10 / 4"), "integer division, not floating point");
            Assert.AreEqual(-3, Eval("-3"));
            Assert.AreEqual(1, Eval("sense + 10 >= 14"));
            Assert.AreEqual(0, Eval("sense + 10 >= 15"));
            Assert.AreEqual(1, Eval("muscle * 2 - 2 == 10"));
        }

        [Test]
        public void AndOrNotChainTheWayTheyRead()
        {
            Assert.AreEqual(1, Eval("muscle > 5 and sense < 5"));
            Assert.AreEqual(0, Eval("muscle > 7 and sense < 5"));
            Assert.AreEqual(1, Eval("muscle > 7 or sense < 5"));
            Assert.AreEqual(1, Eval("not (muscle > 7)"));
            Assert.AreEqual(1, Eval("(muscle > 5 or sense > 9) and perception == 7"));
        }

        [Test]
        public void CarriedAndMoneyAreAvailableToTheCondition()
        {
            Assert.AreEqual(1, Eval("carried(\"battery\")", null, 0, 1, "battery"));
            Assert.AreEqual(0, Eval("carried(\"battery\")"));
            Assert.AreEqual(1, Eval("roubles >= 5000", null, 5000));
            Assert.AreEqual(0, Eval("roubles >= 5000", null, 4999));
            Assert.AreEqual(1, Eval("party == 3", null, 0, 3));
        }

        [Test]
        public void DiceStayInsideTheirRange()
        {
            var random = PythonRandom.Seed(11);
            for (var roll = 0; roll < 200; roll++)
            {
                var value = Eval("1d20", random);
                Assert.GreaterOrEqual(value, 1);
                Assert.LessOrEqual(value, 20);
            }

            var many = PythonRandom.Seed(12);
            for (var roll = 0; roll < 100; roll++)
            {
                var value = Eval("2d6", many);
                Assert.GreaterOrEqual(value, 2);
                Assert.LessOrEqual(value, 12);
            }
        }

        [Test]
        public void ABrokenConditionFailsLoudlyWithItsText()
        {
            var error = Assert.Throws<ValidationException>(() => EventExpression.Compile("muscle >"));
            StringAssert.Contains("muscle >", error.Message);

            Assert.Throws<ValidationException>(() => EventExpression.Compile("unknowable + 1"));
            Assert.Throws<ValidationException>(() => EventExpression.Compile("1d"));
            Assert.Throws<ValidationException>(() => EventExpression.Compile("(muscle > 3"));
        }

        [Test]
        public void DivisionByZeroIsZeroNotAnException()
        {
            // A GM writing sense / party must not crash a raid when the party is empty.
            Assert.AreEqual(0, Eval("muscle / (party - 1)", null, 0, 1));
        }
    }
}
