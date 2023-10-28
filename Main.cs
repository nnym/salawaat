#pragma warning disable 612, 8500
using Gtk;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Action = System.Action;

const string NAME = "salawaat";
const string PRESENT_ACTION = "app.present";
const bool DEBUG = false;

var configBase = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + '/' + NAME;
var configOld = configBase + ".conf";
var config = configBase + ".json";
var tmp = System.IO.Path.GetTempPath() + NAME;

Application application = new("x." + NAME, GLib.ApplicationFlags.None);
application.Register(GLib.Cancellable.Current);

if (application.IsRemote) {
	application.ActivateAction(PRESENT_ACTION, null);
	return 0;
}

Lazy<HttpClient> http = new();
ReaderWriterLockSlim cfgLock = new();

ApplicationWindow window = new(application) {Title = NAME, IconName = Stock.About, DefaultSize = new(320, 220)};
var iconified = false;
window.WindowStateEvent += (_, e) => iconified = ((Gdk.EventWindowState) e.Args[0]).NewWindowState.HasFlag(Gdk.WindowState.Iconified);

var presentAction = new GLib.SimpleAction(PRESENT_ACTION, null);
presentAction.Activated += (_, _) => window.Present();
application.AddAction(presentAction);
application.Activated += (_, _) => {};

Box top = new(Orientation.Vertical, 20) {Margin = 8};
window.Child = top;

var customDate = false;
var time = DateTime.Now;
var coordinates = ("", "");

Label date = new("");
Grid table = new() {Halign = Align.Center, RowSpacing = 8, ColumnSpacing = 8};
Expander settingExpander = new("settings") {Halign = Align.Center};

Box settingBox = new(Orientation.Vertical, 20) {Halign = Align.Center};
Gdk.Rectangle size = new();

settingExpander.Activated += (_, _) => {
	if (settingBox.Visible = settingExpander.Expanded) {
		settingBox.Show();
		window.GetAllocatedSize(out size, out _);
	} else {
		window.Resize(size.Width, size.Height);
	}
};

Grid settings = new() {MarginTop = 8, RowSpacing = 8, ColumnSpacing = 8};
Box buttonRow = new(Orientation.Horizontal, 8);
Button exit = new("_exit") {Hexpand = true};
Button refresh = new("_refresh") {CanDefault = true, Hexpand = true};

exit.Clicked += (_, _) => window.Destroy();
window.Default = refresh;

Prayer? currentPrayer = null, nextPrayer = null;
Prayer[] prayers = {new("fajr"), new("shuruq"), new("dhuhr"), new("'asr"), new("maghrib"), new("'isha")};

for (var i = 0; i < prayers.Length; ++i) {
	var prayer = prayers[i];
	table.Attach(prayer.label, 0, i, 1, 1);
	table.Attach(prayer.value, 1, i, 1, 1);
}

T setting<T>(int row, string name, T input) where T: Widget {
	settings.Attach(new Label(name) {Halign = Align.Start}, 0, row, 1, 1);
	settings.Attach(input, 1, row, 1, 1);

	return input;
}

Entry entrySetting(int row, string name, int maxLength) => setting(row, name, new Entry() {ActivatesDefault = true, MaxLength = maxLength});

var latitude = entrySetting(0, "latitude", 20);
var longitude = entrySetting(1, "longitude", 20);
var alert = setting(2, "notice period in minutes", new SpinButton(0, 999, 1) {Value = 15});
Switch persist = setting(3, "system tray icon", new Switch() {State = true, Halign = Align.Start});
Calendar calendar = new();

calendar.DaySelected += (_, _) => markToday();
calendar.DaySelectedDoubleClick += (_, _) => refresh.Click();

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

StatusIcon icon = new() {Stock = window.IconName, Title = NAME, TooltipText = NAME};
icon.PopupMenu += (_, args) => icon.PresentMenu(menu, (uint) args.Args[0], (uint) args.Args[1]);
icon.Activate += (_, _) => {
	if (settingExpander.Expanded) settingExpander.Activate();
	if (iconified || !window.Visible) window.Present();
	else window.Hide();
};

persist.AddNotification("active", (_, _) => {
	icon.Visible = persist.Active;
	writeConfiguration(readConfiguration() with {statusIcon = persist.Active});
});

window.DeleteEvent += (_, args) => {
	if (icon.Visible) {
		window.Hide();
		args.RetVal = true;
	}
};

window.KeyPressEvent += (_, args) => {
	if (((Gdk.EventKey) args.Args[0]).Key == Gdk.Key.Escape) {
		if (settingExpander.Expanded) settingExpander.Activate();
		else window.Close();
	}
};

refresh.Clicked += (_, _) => {
	customDate = !sameDay(calendar.Date, DateTime.Now);
	load(!customDate);
	if (customDate && coordinates != (latitude.Text, longitude.Text)) load(true);
};

debug("Loading configuration.");

if (File.Exists(configOld)) File.Delete(configOld);

if (File.Exists(config)) {
	var c = readConfiguration();
	latitude.Text = c.latitude;
	longitude.Text = c.longitude;
	alert.Value = c.noticePeriod;
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

return application.Run(application.ApplicationId, new string[0]);


GLib.DateTime toGLib(DateTimeOffset t) => new(t.ToUnixTimeSeconds());
string format(DateTimeOffset t, string format) => toGLib(t).Format(format);
bool sameDay(DateTime a, DateTime b) => a.DayOfYear == b.DayOfYear && a.Year == b.Year;

void setDate(DateTimeOffset t) {
	date.Markup = format(t, $"<b>%A %x{(customDate ? "" : " %X")}</b>");
	if (!customDate) markToday();
}

void idle(Action action) => GLib.Idle.Add(() => {
	action();
	return false;
});

uint timeout(uint delay, Action action) => GLib.Timeout.Add(delay, () => {
	action();
	return false;
});

void debug(string format, params object[] arguments) {
	#pragma warning disable CS0162
	if (DEBUG) Console.WriteLine(format, arguments);
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

void markToday() {
	calendar.ClearMarks();
	if (calendar.Month + 1 == time.Month && calendar.Year == time.Year) calendar.MarkDay((uint) DateTime.Now.Day);
}

Disposable useLock(Action enter, Action exit) {
	enter();
	return new(exit);
}

Configuration readConfiguration() {
	using (useLock(cfgLock.EnterReadLock, cfgLock.ExitReadLock)) return JsonSerializer.Deserialize<Configuration>(File.ReadAllBytes(config));
}

void writeConfiguration(Configuration? configuration = null) {
	configuration ??= new Configuration(latitude.Text, longitude.Text, alert.ValueAsInt, persist.Active);

	using (useLock(cfgLock.EnterWriteLock, cfgLock.ExitWriteLock)) {
		using (var file = File.Open(config, FileMode.OpenOrCreate | FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite)) {
			JsonSerializer.Serialize(file, configuration, new JsonSerializerOptions() {WriteIndented = true});
		}
	}
}

void tick(uint delay) => timeout(delay, () => {
	var now = DateTime.Now;

	if (sameDay(now, time)) {
		time = now;
		if (!customDate) setDate(now);
	} else {
		if (now.Second % 15 == 0) load(newDay: true);
	}

	customDate &= !sameDay(time, calendar.Date);

	if (noticePeriod != (noticePeriod = alert.ValueAsInt)) {
		save();
		alertSent &= nextPrayer == null || time < nextPrayer.today.AddMinutes(-noticePeriod);
	}

	var next = prayers.FirstOrDefault(p => p.today > time);

	if (next != null) {
		if (next != nextPrayer) icon.TooltipText = $"{next.name}: {next.todayValue}";

		if (!alertSent && time >= next.today.AddMinutes(-noticePeriod)) {
			var remaining = next.today - time;

			application.SendNotification("prayer-alert", new("prayer alert") {
				Body = $"{next.name} will be in {remaining.Hours}:{remaining:mm}:{remaining:ss} at {next.todayValue}",
				Priority = GLib.NotificationPriority.High
			});

			alertSent = true;
		}
	} else if (nextPrayer != null) {
		icon.TooltipText = NAME;
	}

	if (next != nextPrayer && nextPrayer != null) {
		application.SendNotification("prayer-time", new("prayer time") {
			Body = $"{nextPrayer.name}: {nextPrayer.todayValue}",
			Priority = GLib.NotificationPriority.High
		});

		alertSent = false;
	}

	if (nextPrayer != (nextPrayer = next)) {
		highlight();
	}

	tick((uint) (1000 - DateTime.Now.Millisecond));
});

void unhighlight() {
	if (currentPrayer != null) {
		currentPrayer.label.Text = currentPrayer.name;
		currentPrayer.value.Text = currentPrayer.value.Text;
		currentPrayer = null;
	}
}

void highlight() {
	var current = prayers.LastOrDefault(p => p.today <= time);
	unhighlight();

	if (!customDate && current != null) {
		currentPrayer = current;
		current.label.Markup = $"<b>{current.name}</b>";
		current.value.Markup = $"<b>{current.value.Text}</b>";
	}
}

void save(object? sender = null, EventArgs? args = null) {
	if (File.Exists(config)) writeConfiguration(readConfiguration() with {noticePeriod = noticePeriod});
	else writeConfiguration();
}

void load(bool updateToday = true, bool newDay = false) => Task.Run(() => {
	lock (window) {
		void idleTask(Action action) {
			Task task = new(action);
			idle(() => task.RunSynchronously());
			task.Wait();
		}

		if (latitude.Text == "" || longitude.Text == "") {
			idleTask(() => {
				if (!settingExpander.Expanded) settingExpander.Activate();
				window.Show();
			});

			return;
		}

		DateTime requestTime = newDay ? DateTime.Now : calendar.Date;
		var path = tmp + '/' + string.Join('-', requestTime.Year, latitude.Text, longitude.Text).Replace('/', '_');
		debug("path " + path);

		Directory.CreateDirectory(tmp);
		Times[]? days = null;
		MessageDialog? error = null;

		if (File.Exists(path)) {
			days = JsonSerializer.Deserialize<Times[]>(File.ReadAllBytes(path), new JsonSerializerOptions() {IncludeFields = true});
		}

		if (days == null) {
			var url = $"https://www.moonsighting.com/time_json.php?year={requestTime.Year}&tz=UTC&lat={latitude.Text}&lon={longitude.Text}&method=2&both=0&time=0";
			debug("URL " + url);

			var response = http.Value.Send(new() {RequestUri = new(url)});
			debug("HTTP {0:d}", response.StatusCode);

			if (response.IsSuccessStatusCode) {
				var json = JsonSerializer.Deserialize<SourceTimes>(response.Content.ReadAsStream(), new JsonSerializerOptions() {IncludeFields = true});
				days = json.times.Select(day => {
					object times = day.times;

					foreach (var t in times.GetType().GetFields()) {
						var value = ((string) t.GetValue(times)!).TrimEnd();
						if (value.StartsWith('0')) value = value.Substring(1);
						t.SetValue(times, value);
					}

					return (Times) times;
				}).ToArray();

				using (var file = File.OpenWrite(path)) JsonSerializer.Serialize(file, days, new JsonSerializerOptions() {IncludeFields = true});
			} else {
				error = new(window, 0, MessageType.Error, ButtonsType.Close, false, "Server responded with code {0:d}. Request URL is {1}.", response.StatusCode, url);
			}
		}

		if (days == null) {
			error ??= new(window, 0, MessageType.Error, ButtonsType.Close, false, "An unknown error occurred.");
			idleTask(() => error.Present());

			return;
		}

		writeConfiguration();
		customDate &= !newDay;

		var day = days[requestTime.DayOfYear - 1];

		idleTask(() => {
			for (var i = 0; i < prayers.Length; ++i) {
				ref var prayer = ref prayers[i];
				var time = requestTime.Date + DateTime.Parse(day.get(i)).TimeOfDay + TimeZoneInfo.Local.GetUtcOffset(requestTime);
				var output = Regex.Replace(format(time, "%R"), "^0", "");

				if (updateToday) {
					prayer.today = time;
					prayer.todayValue = output;
				}
				
				if (!customDate || !updateToday) {
					prayer.target = time;
					prayer.value.Text = output;
				}
			}

			if (customDate) {
				setDate(requestTime);
				unhighlight();
			} else {
				setDate(time = DateTime.Now);
			}

			calendar.Date = requestTime;
			coordinates = (latitude.Text, longitude.Text);

			highlight();
		});
	}
}).ContinueWith(task => {
	if (task.IsFaulted) Console.Error.WriteLine(task.Exception);
});

public record Disposable(Action dispose) : IDisposable {
    public void Dispose() => this.dispose();
}

public record struct Configuration(string latitude, string longitude, int noticePeriod, bool statusIcon) {}

public record Prayer(string name) {
	public DateTime today;
	public DateTime target;
	public string todayValue;
	public Label label = new(name) {Halign = Align.Start};
	public Label value = new("--:--");
}

public struct SourceTimes {
	public Query? query;
	public Day[] times;
}

public struct Query {
	public string latitude, longitude, timeZone, method, year, both, time;
}

public struct Day {
	public string day;
	public Times times;
}

public unsafe record struct Times() {
	public string fajr, sunrise, dhuhr, asr, maghrib, isha;
	public string? asr_s, asr_h;

	public unsafe string get(int index) => ((string*) Unsafe.AsPointer(ref this.fajr))[index];
}
