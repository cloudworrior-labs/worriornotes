using Microsoft.Playwright;
using WorriorVex.Application.Notes;
using static Microsoft.Playwright.Assertions;

namespace WorriorVex.UITests;

[Collection("browser")]
public sealed class CalendarTests(BrowserFixture browser) : UITest(browser)
{
    [Fact]
    public async Task Several_notes_are_typed_into_a_day_edited_and_opened()
    {
        await Nav("Month view").ClickAsync();
        var today = Page.Locator(".wn-cal-day.is-today");
        await Expect(Page.Locator(".wn-cal-day")).ToHaveCountAsync(42);

        // Click the day, type, Enter; the box stays for the next one.
        await today.ClickAsync();
        await Expect(today.Locator(".wn-inline-rename")).ToBeFocusedAsync();
        await Page.Keyboard.TypeAsync("Dentist 10:00");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(today.Locator(".wn-cal-entry")).ToHaveCountAsync(1);
        await Expect(today.Locator(".wn-inline-rename")).ToBeFocusedAsync();
        await Page.Keyboard.TypeAsync("Call supplier");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(today.Locator(".wn-cal-entry")).ToHaveCountAsync(2);
        await Page.Keyboard.PressAsync("Escape");

        // Click an entry to change its words.
        await today.Locator(".wn-cal-entry", new LocatorLocatorOptions { HasTextString = "Dentist" }).ClickAsync();
        await Page.FillAsync(".wn-cal-day.is-today .wn-inline-rename", "Dentist 11:30");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(today.Locator(".wn-cal-entry").First).ToHaveTextAsync("Dentist 11:30");

        var notes = await App.Get<INoteService>().ListAllAsync();
        Assert.Equal(["Call supplier", "Dentist 11:30"], notes.Select(n => n.Title).OrderBy(t => t));

        // ↗ opens the note in the editor, in the Calendar notebook.
        var row = today.Locator(".wn-cal-entryrow", new LocatorLocatorOptions { HasTextString = "Call supplier" });
        await row.HoverAsync();
        await row.Locator(".wn-cal-action").First.ClickAsync();
        await Expect(Page.Locator(".wn-title")).ToHaveValueAsync("Call supplier");
        await Expect(Page.Locator(".wn-notelist h2")).ToHaveTextAsync("All calendar notes");

        // Next month and back.
        await Nav("Month view").ClickAsync();
        await Expect(Page.Locator(".wn-cal-day")).ToHaveCountAsync(42);
        var title = await Page.Locator("#wn-calendar-title").InnerTextAsync();
        await Page.ClickAsync(".wn-cal-nav button[aria-label='Next month']");
        await Expect(Page.Locator("#wn-calendar-title")).Not.ToHaveTextAsync(title);
        var todayButton = Page.Locator(".wn-cal-nav button", new PageLocatorOptions { HasTextString = "Today" });
        await Expect(todayButton).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await todayButton.ClickAsync();
        await Expect(Page.Locator("#wn-calendar-title")).ToHaveTextAsync(title);
        await Expect(Page.Locator(".wn-cal-day.is-today .wn-cal-entry")).ToHaveCountAsync(2);
    }
}

[Collection("browser")]
public sealed class TemplateFromHelpPageTests(BrowserFixture browser) : UITest(browser)
{
    [Fact]
    public async Task A_template_note_made_while_on_the_calendar_opens_in_the_inbox()
    {
        await Nav("Month view").ClickAsync();
        await Expect(Page.Locator(".wn-cal-day")).ToHaveCountAsync(42);

        await Page.ClickAsync(".wn-topbar button[aria-label='New note from a template']");
        await Page.ClickAsync(".wn-picker-list .wn-noteitem:has-text('Meeting notes')");

        await Expect(Page.Locator(".wn-title")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("^Meeting notes "));
        await Expect(Page.Locator(".wn-prose h2").First).ToHaveTextAsync("Attendees");
        await Expect(Page.Locator(".wn-notelist h2")).ToHaveTextAsync("Inbox");
        await Expect(Page.Locator(".wn-prose input[type=checkbox]")).ToHaveCountAsync(1);

        // Typing into it saves normally afterwards.
        await Page.Locator(".wn-prose").ClickAsync();
        await Page.Keyboard.PressAsync("End");
        await Page.Keyboard.TypeAsync(" decided");
        await WaitForSavedAsync();
        var notes = await App.Get<INoteService>().ListAllAsync();
        Assert.Contains(notes, n => n.Title.StartsWith("Meeting notes "));
        Assert.DoesNotContain(App.Logs, l => l.Contains("[Error]") || l.Contains("[Critical]"));
    }
}

[Collection("browser")]
public sealed class CalendarNavigationTests(BrowserFixture browser) : UITest(browser)
{
    [Fact]
    public async Task Leaving_the_calendar_after_a_note_was_open_does_not_break_the_page()
    {
        // A note is open (so an editor existed), then the calendar replaces it.
        var notes = App.Get<INoteService>();
        await notes.CreateAsync(title: "Open first", content: "<p>x</p>");
        await Page.ReloadAsync();
        await Page.Locator(".wn-noteitem:has-text('Open first')").ClickAsync();
        await Page.Locator(".wn-title").WaitForAsync();

        await Nav("Month view").ClickAsync();
        var today = Page.Locator(".wn-cal-day.is-today");
        await today.ClickAsync();
        await Expect(today.Locator(".wn-inline-rename")).ToBeFocusedAsync();
        await Page.Keyboard.TypeAsync("Deadline");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(today.Locator(".wn-cal-entry")).ToHaveCountAsync(1);

        // Then anything that saves before leaving: another page, and a new notebook.
        await Nav("All Notes").ClickAsync();
        await Expect(Page.Locator(".wn-notelist h2")).ToHaveTextAsync("All Notes");
        await Nav("Month view").ClickAsync();
        await Page.ClickAsync(".wn-nav button[title='New notebook']");
        await Page.FillAsync("#wn-prompt-input", "After the calendar");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Nav("After the calendar")).ToHaveCountAsync(1);

        await Expect(Page.Locator(".wn-fatal")).ToHaveCountAsync(0);
        Assert.DoesNotContain(App.Logs, l => l.Contains("[Critical]") || l.Contains("ObjectDisposed"));

        // The calendar has its own heading; its notebook is not among the notebooks.
        await Expect(Page.Locator("#wn-calendar-heading")).ToHaveTextAsync("Calendar");
        await Expect(Page.Locator("[aria-labelledby='wn-notebooks-heading'] .wn-navitem:text-is('Calendar')")).ToHaveCountAsync(0);
        await Nav("All calendar notes").ClickAsync();
        await Expect(Page.Locator(".wn-notelist .wn-noteitem-title")).ToHaveTextAsync("Deadline");
    }
}
