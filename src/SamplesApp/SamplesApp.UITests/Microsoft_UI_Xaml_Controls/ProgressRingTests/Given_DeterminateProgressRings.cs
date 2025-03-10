﻿using System;
using System.Linq;
using NUnit.Framework;
using SamplesApp.UITests.TestFramework;
using Uno.UITest.Helpers;
using Uno.UITest.Helpers.Queries;

namespace SamplesApp.UITests.Microsoft_UI_Xaml_Controls.ProgressRingTests
{
	public partial class Given_DeterminateProgressRings : SampleControlUITestBase
	{
		private const string red = "#FF0000";
		private const string green = "#008000";
		private readonly ExpectedColor[] _expectedColors = new[]
		{
			new ExpectedColor { Value = 0, Colors = new [] { red, red, red, red } },
			new ExpectedColor { Value = 25, Colors = new [] { red, green, red, red } },
			new ExpectedColor { Value = 50, Colors = new [] { red, green, green, red } },
			new ExpectedColor { Value = 75, Colors = new [] { red, green, green, green } },
			new ExpectedColor { Value = 100, Colors = new [] { green, green, green, green } },
		};

		[Test]
		[AutoRetry]
		public void Detereminate_ProgressRing_Validation()
		{
			Run("UITests.Microsoft_UI_Xaml_Controls.ProgressRing.WinUIDeterminateProgressRing");

			_app.WaitForElement("ProgressRing");

			var topLeftTargetRect = _app.GetPhysicalRect("TopLeftTarget");
			var topRightTargetRect = _app.GetPhysicalRect("TopRightTarget");
			var bottomLeftTargetRect = _app.GetPhysicalRect("BottomLeftTarget");
			var bottomRightTargetRect = _app.GetPhysicalRect("BottomRightTarget");

			foreach (var expected in _expectedColors)
			{
				SetComboBox("ProgressValue", expected.Value.ToString());

				_app.Wait(TimeSpan.FromSeconds(5)); //Wait for animations to finish

				using var snapshot = TakeScreenshot($"Progress-Ring-Value-{expected.Value}");

				ImageAssert.HasPixels(
					snapshot,
					ExpectedPixels
						.At($"top-left-{expected.Value}-progress", topLeftTargetRect.CenterX, topLeftTargetRect.CenterY)
						.WithPixelTolerance(1, 1)
						.Pixel(expected.Colors[0]),
					ExpectedPixels
						.At($"top-right-{expected.Value}-progress", topRightTargetRect.CenterX, topRightTargetRect.CenterY)
						.WithPixelTolerance(1, 1)
						.Pixel(expected.Colors[1]),
					ExpectedPixels
						.At($"bottom-right-{expected.Value}-progress", bottomRightTargetRect.CenterX, bottomRightTargetRect.CenterY)
						.WithPixelTolerance(1, 1)
						.Pixel(expected.Colors[2]),
					ExpectedPixels
						.At($"bottom-left-{expected.Value}-progress", bottomLeftTargetRect.CenterX, bottomLeftTargetRect.CenterY)
						.WithPixelTolerance(1, 1)
						.Pixel(expected.Colors[3])
				);
			}
		}

		private void SetComboBox(string comboBoxName, string item)
		{
			Console.WriteLine("Setting '" + comboBoxName + "' to '" + item + "'");
			var comboBox = _app.Marked(comboBoxName);
			var _ = comboBox.SetDependencyPropertyValue("SelectedItem", item);
		}

		private struct ExpectedColor
		{
			public int Value { get; set; }

			//Expected colors at quadrants (top-left, top-right, bottom-left, bottom-right)
			public string[] Colors { get; set; }
		}

		[Test]
		[AutoRetry]
		public void TestProgressRing_InitialState()
		{
			Run("UITests.Microsoft_UI_Xaml_Controls.ProgressRing.WinUIProgressRing_Features");

			_app.WaitForElement("dyn8");

			using var screenshot = TakeScreenshot("scrn", ignoreInSnapshotCompare: true);

			_app.Marked("dynamicValue").SetDependencyPropertyValue("Value", "90");
			_app.Marked("dynamicValue").SetDependencyPropertyValue("Value", "30");

			using var screenshot2 = TakeScreenshot("scrn2", ignoreInSnapshotCompare: true);

			var rects = Enumerable
				.Range(1, 8)
				.Select(i => "dyn" + i)
				.Select(marked => _app.GetPhysicalRect(marked))
				.ToArray();

			var i = 1;
			foreach (var rect in rects)
			{
				ImageAssert.AreNotEqual(screenshot2, screenshot, rect);
				_app.Marked("dyn" + i++).SetDependencyPropertyValue("Opacity", "0");
			}

			using var screenshot3 = TakeScreenshot("scrn3", ignoreInSnapshotCompare: true);

			foreach (var rect in rects)
			{
				// Ensure initial state is not empty
				ImageAssert.AreNotEqual(screenshot3, screenshot, rect);
			}
		}
	}
}
