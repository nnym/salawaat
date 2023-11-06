#pragma warning disable 612, 8500
using DesktopNotifications;
using DesktopNotifications.FreeDesktop;
using DesktopNotifications.Windows;
using Gdk;
using Gtk;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Action = System.Action;
using SpecialFolder = System.Environment.SpecialFolder;
using static Const;

if (OperatingSystem.IsWindows()) {
	var assembly = Assembly.GetExecutingAssembly();
	var v = typeof(Application).Assembly.GetName().Version!;
	var gtkDirectory = Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "Gtk", $"{v.Major}.{v.Minor}.{v.Build}");

	foreach (var path in assembly.GetManifestResourceNames().Where(r => r.StartsWith("gtk/"))) {
		using var @in = assembly.GetManifestResourceStream(path)!;
		var dst = gtkDirectory + path[3..];

		if (!File.Exists(dst)) {
			Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
			using var @out = File.OpenWrite(dst);
			@in.CopyTo(@out);
		}
	}
}

var config = Environment.GetFolderPath(SpecialFolder.ApplicationData) + $"/{NAME}.json";
var tmp = System.IO.Path.GetTempPath() + NAME;

Application application = new(ID, GLib.ApplicationFlags.None);
application.Register(GLib.Cancellable.Current);

if (application.IsRemote) {
	application.ActivateAction(PRESENT_ACTION, null);
	return 0;
}

INotificationManager? notifMan = OperatingSystem.IsLinux() ? new FreeDesktopNotificationManager()
	: OperatingSystem.IsWindows() ? new WindowsNotificationManager()
	: null;

if (notifMan != null) await notifMan.Initialize();
debug("notifMan = {0}", notifMan);

var notifDuration = TimeSpan.FromSeconds(30);
var loading = Task.CompletedTask;
Lazy<HttpClient> http = new();
ReaderWriterLockSlim cfgLock = new();

ApplicationWindow window = new(application) {Title = NAME, Icon = Pixbuf.LoadFromResource("icon.png"), DefaultSize = new(320, 220)};
var iconified = false;
window.WindowStateEvent += (_, e) => iconified = ((EventWindowState) e.Args[0]).NewWindowState.HasFlag(WindowState.Iconified);

var presentAction = new GLib.SimpleAction(PRESENT_ACTION, null);
presentAction.Activated += (_, _) => window.Present();
application.AddAction(presentAction);
application.Activated += (_, _) => debug("activated");

Box top = new(Orientation.Vertical, 20) {Margin = 8};
window.Child = top;

var showToday = true;
var time = DateTime.Now;
var coordinates = ("", "");

Label date = new("");
Grid table = new() {Halign = Align.Center, RowSpacing = 8, ColumnSpacing = 8};
Expander settingExpander = new("settings") {Halign = Align.Center};

Box settingBox = new(Orientation.Vertical, 20) {Halign = Align.Center};
Grid settings = new() {MarginTop = 8, RowSpacing = 8, ColumnSpacing = 8};
Box buttonRow = new(Orientation.Horizontal, 8);
Button exit = new("_exit") {Hexpand = true};
Button refresh = new("_refresh") {CanDefault = true, Hexpand = true};

exit.Clicked += (_, _) => window.Destroy();
window.Default = refresh;

Prayer? currentPrayer = null, nextPrayer = null;
Prayer[] prayers = {new("fajr"), new("shuruq"), new("dhuhr"), new("'asr"), new("maghrib"), new("'isha")};
fillTable(0);

var latitude = entrySetting(0, "latitude", 20);
var longitude = entrySetting(1, "longitude", 20);
var alert = setting(2, "notice period in minutes", new SpinButton(0, 999, 1) {Value = 15});
Switch relative = setting(3, "relative times", new Switch() {State = true, Halign = Align.Start});
Switch persist = setting(4, "system tray icon", new Switch() {State = true, Halign = Align.Start});
Calendar calendar = new();

calendar.DaySelected += (_, _) => markToday();
calendar.DaySelectedDoubleClick += (_, _) => {
	relative.State = false;
	refresh.Click();
};

add(buttonRow, exit, refresh);
add(settingBox, settings, calendar, buttonRow);
add(top, date, table, settingExpander);

showDescendants(settingBox);
showDescendants(window);
top.Add(settingBox);

MenuItem quit = new("_exit");
quit.Activated += (_, _) => window.Destroy();

Menu menu = new() {Child = quit};
menu.ShowAll();

StatusIcon icon = new() {Icon = window.Icon, Title = NAME, TooltipText = NAME};
icon.PopupMenu += (_, args) => icon.PresentMenu(menu, (uint) args.Args[0], (uint) args.Args[1]);
icon.Activate += (_, _) => {
	if (iconified || !window.Visible) window.Present();
	else window.Hide();
};

window.DeleteEvent += (_, args) => {
	if (icon.Visible) {
		window.Hide();
		if (settingExpander.Expanded) settingExpander.Activate();
		args.RetVal = true;
	}
};

window.KeyPressEvent += (_, args) => {
	if (args.Args[0] is EventKey {Key: Gdk.Key.Escape}) {
		if (settingExpander.Expanded) settingExpander.Activate();
		else window.Close();
	}
};

Rectangle size = new();

settingExpander.Activated += (_, _) => {
	if (settingBox.Visible = settingExpander.Expanded) {
		settingBox.Show();
		window.GetAllocatedSize(out size, out _);
	} else {
		window.Resize(size.Width, size.Height);
	}
};

persist.AddNotification("active", (_, _) => {
	icon.Visible = persist.Active;
	writeConfiguration(readConfiguration() with {statusIcon = persist.Active});
});

relative.AddNotification("state", (_, _) => {
	load(setToday: relative.State);
	writeConfiguration(readConfiguration() with {relative = relative.Active});
});

refresh.Clicked += (_, _) => {
	showToday = sameDay(calendar.Date, DateTime.Now);
	if (!showToday) relative.State = false;
	load(showToday);
	if (!showToday && coordinates != (latitude.Text, longitude.Text)) load(true);
};

debug("Loading configuration.");

if (File.Exists(config)) {
	var c = readConfiguration();
	latitude.Text = c.latitude;
	longitude.Text = c.longitude;
	alert.Value = c.noticePeriod;
	relative.State = c.relative;
	icon.Visible = persist.Active = c.statusIcon;
}

if (Environment.GetCommandLineArgs().Contains("--hidden")) {
	persist.Active = true;
} else {
	window.Present();
}

var alertSent = false;
var noticePeriod = alert.ValueAsInt;

load();
tick(0);

AppDomain.CurrentDomain.ProcessExit += save;
AppDomain.CurrentDomain.UnhandledException += save;
Console.CancelKeyPress += save;

return application.Run(application.ApplicationId, [..Environment.GetCommandLineArgs()[1..]]);


GLib.DateTime toGLib(DateTimeOffset t) => new(t.ToUnixTimeSeconds());
string format(DateTimeOffset t, string format) => toGLib(t).Format(format);
bool sameDay(DateTime a, DateTime b) => a.DayOfYear == b.DayOfYear && a.Year == b.Year;

void setDate(DateTimeOffset t) {
	date.Markup = format(t, $"<b>%A %x{(showToday ? " %X" : "")}</b>");
	if (showToday) markToday();
}

void idle(Action action) => GLib.Idle.Add(() => {
	action();
	return false;
});

uint timeout(uint delay, Action action) => GLib.Timeout.Add(delay, () => {
	action();
	return false;
});

void idleTask(Action action) {
	Task task = new(action);
	idle(() => task.RunSynchronously());
	task.Wait();
}

void debug(object? format, params object?[] arguments) {
	#pragma warning disable CS0162
	if (DEBUG) {
		if (format is string s) Console.WriteLine(s, arguments);
		else Console.WriteLine(string.Join(" ", format is object?[] os ? [..os, ..arguments] : [format, ..arguments]));
	}
	#pragma warning restore CS0162
}

#pragma warning disable 8321
void warning(string format, params object[] arguments) => Console.Error.WriteLine(format, arguments);

T p<T>(T o) {
	Console.WriteLine(o);
	return o;
}
#pragma warning restore 8321

void add(Container parent, params Widget[] children) {
	foreach (var child in children) parent.Add(child);
}

void showDescendants(Container c) => c.Forall(w => w.ShowAll());

void fillTable(int offset) {
	table.Forall(table.Remove);

	foreach (var i in ..prayers.Length) {
		var prayer = prayers[(offset + i) % prayers.Length];
		table.Attach(prayer.label, 0, i, 1, 1);
		table.Attach(prayer.displayValue, 1, i, 1, 1);
	}
}

T setting<T>(int row, string name, T input) where T: Widget {
	settings.Attach(new Label(name) {Halign = Align.Start}, 0, row, 1, 1);
	settings.Attach(input, 1, row, 1, 1);

	return input;
}

Entry entrySetting(int row, string name, int maxLength) => setting(row, name, new Entry() {ActivatesDefault = true, MaxLength = maxLength});

void markToday() {
	calendar.ClearMarks();
	if (calendar.Month + 1 == time.Month && calendar.Year == time.Year) calendar.MarkDay((uint) DateTime.Now.Day);
}

Disposable useLock(Action enter, Action exit) {
	enter();
	return new(exit);
}

Configuration readConfiguration() {
	using var _ = useLock(cfgLock.EnterReadLock, cfgLock.ExitReadLock);
	return JsonSerializer.Deserialize<Configuration>(File.ReadAllBytes(config));
}

void writeConfiguration(Configuration? configuration = null) {
	configuration ??= new Configuration(latitude.Text, longitude.Text, alert.ValueAsInt, persist.Active, relative.Active);

	using var _ = useLock(cfgLock.EnterWriteLock, cfgLock.ExitWriteLock);
	using var file = File.Open(config, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
	JsonSerializer.Serialize(file, configuration, new JsonSerializerOptions() {WriteIndented = true});
}

void tick(uint delay) => timeout(delay, () => {
	var now = DateTime.Now;

	if (sameDay(now, time)) {
		time = now;
		if (showToday) setDate(now);
	} else {
		if (now.Second % 15 == 0) load(setToday: true);
	}

	showToday |= sameDay(time, calendar.Date);

	if (noticePeriod != (noticePeriod = alert.ValueAsInt)) {
		save();
		alertSent &= nextPrayer == null || time < nextPrayer.today.AddMinutes(-noticePeriod);
	}

	var lines = new string[2];
	var next = prayers.Where(p => p.today > time).MinBy(p => p.today - time);

	if (next != null) {
		var remaining = next.today - time;
		lines[1] = $"{next.name} will be at {next.todayValue} in {remaining.Hours}:{remaining:mm}:{remaining:ss}";

		if (!alertSent && time >= next.today.AddMinutes(-noticePeriod)) {
			debug("advance notification");
			if (notifMan != null) notifMan.ShowNotification(new() {Title = "prayer alert", Body = lines[1]}, time + notifDuration);
			alertSent = true;
		}
	} else if (nextPrayer != null) {
		icon.TooltipText = NAME;
	}

	if (next != nextPrayer && nextPrayer != null) {
		debug("arrival notification");
		if (notifMan != null) notifMan.ShowNotification(new() {Title = "prayer time", Body = $"{nextPrayer.name}: {nextPrayer.todayValue}"}, time + notifDuration);
		alertSent = false;
		load();
	}

	if (nextPrayer != (nextPrayer = next)) highlight();
	if (currentPrayer != null) lines[0] = $"{currentPrayer.name} since {currentPrayer.todayValue}";

	icon.TooltipText = string.Join("\n", lines.Where(l => l != null));
	tick((uint) (1000 - DateTime.Now.Millisecond));
});

void unhighlight() {
	if (currentPrayer != null) {
		currentPrayer.label.Text = currentPrayer.name;
		currentPrayer.displayValue.Text = currentPrayer.displayValue.Text;
		currentPrayer = null;
	}
}

void highlight() {
	unhighlight();

	if (showToday && prayers.LastOrDefault(p => time >= p.display) is Prayer current) {
		currentPrayer = current;
		current.label.Markup = $"<b>{current.name}</b>";
		current.displayValue.Markup = $"<b>{current.displayValue.Text}</b>";
	}
}

void save(object? sender = null, EventArgs? args = null) {
	if (File.Exists(config)) writeConfiguration(readConfiguration() with {noticePeriod = noticePeriod});
	else writeConfiguration();
}

Times[]? loadYear(DateTime requestTime) {
	debug("Loading year {0}.", requestTime.Year);

	if (latitude.Text == "" || longitude.Text == "") {
		idleTask(() => {
			if (!settingExpander.Expanded) settingExpander.Activate();
			window.Show();
		});

		return null;
	}

	var path = tmp + '/' + string.Join('-', requestTime.Year, latitude.Text, longitude.Text).Replace('/', '_');
	debug("path " + path);

	Directory.CreateDirectory(tmp);
	Times[]? year = null;
	MessageDialog? error = null;

	if (File.Exists(path)) {
		year = JsonSerializer.Deserialize<Times[]>(File.ReadAllBytes(path), new JsonSerializerOptions() {IncludeFields = true});
	}

	if (year == null) {
		var url = $"https://www.moonsighting.com/time_json.php?year={requestTime.Year}&tz=UTC&lat={latitude.Text}&lon={longitude.Text}&method=2&both=0&time=0";
		debug("URL " + url);

		var response = http.Value.Send(new(HttpMethod.Get, url));
		debug("HTTP {0:d}", response.StatusCode);

		if (response.IsSuccessStatusCode) {
			var json = JsonSerializer.Deserialize<SourceTimes>(response.Content.ReadAsStream(), new JsonSerializerOptions() {IncludeFields = true});
			year = json.times.Select(day => {
				var times = day.times;

				foreach (var t in times.GetType().GetFields()) {
					var value = ((string) t.GetValue(times)!).TrimEnd();
					if (value.StartsWith('0')) value = value.Substring(1);
					t.SetValue(times, value);
				}

				return times;
			}).ToArray();

			using var file = File.OpenWrite(path);
			JsonSerializer.Serialize(file, year, new JsonSerializerOptions() {IncludeFields = true});
		} else {
			error = new(window, 0, MessageType.Error, ButtonsType.Close, false, "Server responded with code {0:d}. Request URL is {1}.", response.StatusCode, url);
		}
	}

	if (year == null) {
		error ??= new(window, 0, MessageType.Error, ButtonsType.Close, false, "An unknown error occurred.");
		idleTask(() => error.Present());

		return null;
	}

    return year;
}

void load(bool today = true, bool setToday = false) => loading = loading.ContinueWith(_ => {
	var requestTime = setToday ? DateTime.Now : calendar.Date;

	if (loadYear(requestTime) is not Times[] year) return;

	var day = year[requestTime.DayOfYear - 1];
	writeConfiguration();
	showToday |= setToday;

	idleTask(() => {
		DateTime parseTime(DateTime date, string time) => (date = date.Date + DateTime.Parse(time).TimeOfDay) + TimeZoneInfo.Local.GetUtcOffset(date);

		void setDay(bool wrap, DateTime date, Times times, int i) {
			ref var prayer = ref prayers[i];
			var time = parseTime(date, times.get(i));
			var output = Regex.Replace(format(time, "%R"), "^0", "");

			if (today) {
				prayer.today = time;
				prayer.todayValue = output;
			}
			
			if (!today || showToday && (!wrap || relative.State)) {
				prayer.display = time;
				prayer.displayValue.Text = output;
			}
		}

		bool wrap(int direction, int time) {
			var day = requestTime.AddDays(direction);
			var y = year;

			if (day.Year != requestTime.Year) {
				if (loadYear(day) is Times[] y1) y = y1;
				else {
					relative.State = false;
					return false;
				}
			}

			setDay(true, day, y[day.DayOfYear - 1], time);
			return true;
		}

		foreach (var i in ..prayers.Length) setDay(false, requestTime, day, i);

		if (today) {
			var offset = Enumerable.Range(0, prayers.Length).Where(i => DateTime.Now >= prayers[i].today).LastOrDefault();
			var ok = true;

			if (relative.State) foreach (var i in ..offset) ok &= wrap(1, i);
			if (offset == 0 && DateTime.Now < prayers[0].today) ok &= wrap(-1, offset = prayers.Length - 1);
			if (ok) fillTable(relative.State ? offset : 0);
		}
		
		coordinates = (latitude.Text, longitude.Text);
		nextPrayer = null;
		calendar.Date = requestTime;
		setDate(showToday ? time = DateTime.Now : requestTime);
		highlight();
	});
}).ContinueWith(task => {
	if (task.IsFaulted) Console.Error.WriteLine(task.Exception);
});

class Const {
	public const bool DEBUG = true;
	public const string
		NAME = "salawaat",
		ID = "x." + NAME,
		NOTIFICATIONS = "org.freedesktop.Notifications",
		PRESENT_ACTION = "app.present";
}

public record Disposable(Action dispose) : IDisposable {
	public void Dispose() => this.dispose();
}

public record struct Configuration(string latitude, string longitude, int noticePeriod, bool relative, bool statusIcon);
public record struct SourceTimes(Day[] times);
public record struct Day(Times times);

public record Prayer(string name) {
	public DateTime today, display;
	public string todayValue;
	public Label label = new(name) {Halign = Align.Start};
	public Label displayValue = new("--:--");
}

public class Times {
	public string fajr, sunrise, dhuhr, asr, maghrib, isha;

	public unsafe string get(int index) => ((string*) Unsafe.AsPointer(ref this.fajr))[index];
}
