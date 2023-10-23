#pragma warning disable 612
using Gtk;
using System.Text.Json;
using Action = System.Action;

const string NAME = "salawaat";
const bool DEBUG = false;

var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + '/' + NAME + ".conf";
var tmp = System.IO.Path.GetTempPath() + NAME;

Application application = new("x." + NAME, GLib.ApplicationFlags.None);
application.Register(GLib.Cancellable.Current);
application.Activated += (_, _) => {};

ApplicationWindow window = new(application) {Title = NAME, IconName = Stock.About, DefaultSize = new(320, 220)};

Box top = new(Orientation.Vertical, 20);
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
		settingBox.ShowAll();
		window.GetAllocatedSize(out size, out _);
	} else {
		window.Resize(size.Width, size.Height);
	}
};

Grid settings = new() {MarginTop = 8, RowSpacing = 8, ColumnSpacing = 8};
Box buttonRow = new(Orientation.Horizontal, 8) {MarginBottom = 8};
Button exit = new("exit") {Hexpand = true};
Button refresh = new("refresh") {CanDefault = true, Hexpand = true};

exit.Clicked += (_, _) => window.Destroy();
window.Default = refresh;

var nextPrayer = -1;
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
Switch persist = setting(2, "system tray icon", new Switch() {State = true, Halign = Align.Start});
Calendar calendar = new();

calendar.DaySelected += (_, _) => markToday();
calendar.DaySelectedDoubleClick += (_, _) => refresh.Click();

add(buttonRow, exit, refresh);
add(settingBox, settings, calendar, buttonRow);
add(top, date, table, settingExpander);

window.ShowAll();
top.Add(settingBox);

MenuItem quit = new("exit");
quit.Activated += (_, _) => window.Destroy();

Menu menu = new() {Child = quit};
menu.ShowAll();

StatusIcon icon = new() {Stock = window.IconName, Title = NAME, TooltipText = NAME};
icon.PopupMenu += (_, args) => icon.PresentMenu(menu, (uint) args.Args[0], (uint) args.Args[1]);
icon.Activate += (_, _) => {
	if (settingExpander.Expanded) settingExpander.Activate();
	window.Visible ^= true;
};

persist.AddNotification("active", (_, _) => {
	icon.Visible = persist.Active;
	writeConfiguration();
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
	load(updateToday: coordinates != (latitude.Text, longitude.Text));
};

tick(0);
load(true);

return application.Run(application.ApplicationId, new string[0]);


GLib.DateTime toGLib(DateTimeOffset t) => new(t.ToUnixTimeSeconds());
string format(DateTimeOffset t, string format) => toGLib(t).Format(format);
bool sameDay(DateTime a, DateTime b) => a.DayOfYear == b.DayOfYear && a.Year == b.Year;

void setDate(DateTimeOffset t) {
	date.Markup = format(t, $"<b>%A %{(customDate ? "x" : "c")}</b>");
	if (!customDate) markToday();
}

void idle(Action action) => GLib.Idle.Add(() => {
	action();
	return false;
});

void timeout(uint delay, Action action) => GLib.Timeout.Add(delay, () => {
	action();
	return false;
});

void debug(string format, params object[] arguments) {
	#pragma warning disable CS0162
	if (DEBUG) Console.WriteLine(format, arguments);
	#pragma warning restore CS0162
}

void warning(string format, params object[] arguments) => Console.Error.WriteLine(format, arguments);

#pragma warning disable 8321
T p<T>(T o) {
	Console.WriteLine(o);
	return o;
}
#pragma warning restore 8321

void add(Container parent, params Widget[] children) {
	foreach (var child in children) parent.Add(child);
}

void markToday() {
	calendar.ClearMarks();
	if (calendar.Month + 1 == time.Month && calendar.Year == time.Year) calendar.MarkDay((uint) DateTime.Now.Day);
}

void highlight() {
	var next = 0;

	while (prayers[next].today <= time) {
		if (++next == prayers.Length) {
			next = -1;
			break;
		}
	}

	if (next != nextPrayer) {
		resetHighlight();
		nextPrayer = next;

		if (!customDate && next >= 0) {
			var prayer = prayers[next];
			prayer.label.Markup = $"<b>{prayer.name}</b>";
			prayer.value.Markup = $"<b>{prayer.value.Text}</b>";
			icon.TooltipText = $"{prayer.name}: {prayer.value.Text}";
		}
	}

	if (nextPrayer >= 0) {
		var prayer = prayers[nextPrayer];
		var remaining = (prayer.today - time).TotalMilliseconds;

		if (remaining >= 0 && remaining < 1000) {
			timeout((uint) remaining, () => application.SendNotification("prayer-time", new("prayer time") {Body = $"{prayer.name}: {prayer.value.Text}"}));
		}
	}
}

void resetHighlight() {
	if (nextPrayer >= 0) {
		var old = prayers[nextPrayer];
		old.label.Text = old.name;
		old.value.Text = old.value.Text;
	}
}

void writeConfiguration() => File.WriteAllText(config, string.Join('\n', latitude.Text, longitude.Text, persist.Active));

void tick(uint delay) => timeout(delay, () => {
	if (customDate) resetHighlight();

	var now = DateTime.Now;

	if (!sameDay(now, time)) {
		if (now.Second % 15 == 0) load(false);
	} else if (!customDate) setDate(time = now);

	highlight();
	tick((uint) (1000 - DateTime.Now.Microsecond / 1000));
});

void load(bool loadConfiguration = false, bool updateToday = true) => Task.Run(() => {
	lock (window) {
		Task enbuffer(Action action) {
			Task task = new(action);
			idle(() => task.RunSynchronously());

			return task;
		}

		if (loadConfiguration) {
			debug("Loading configuration.");

			if (File.Exists(config)) {
				var contents = File.ReadAllLines(config).ToArray();

				enbuffer(() => {
					if (contents.Length > 0) latitude.Text = contents[0];
					if (contents.Length > 1) longitude.Text = contents[1];
					if (contents.Length > 2) icon.Visible = persist.Active = contents[2] != "False";
					if (contents.Length != 3) warning("Configuration file is corrupt.");
				}).Wait();
			}
		}

		if (latitude.Text == "" || longitude.Text == "") {
			enbuffer(() => {
				if (!settingExpander.Expanded) settingExpander.Activate();
			});

			return;
		}

		DateTime requestTime = calendar.Date;
		var path = tmp + '/' + string.Join('-', requestTime.Year, latitude.Text, longitude.Text).Replace('/', '_');
		debug("path " + path);

		Directory.CreateDirectory(tmp);
		Times[]? days = null;
		MessageDialog? alert = null;

		if (File.Exists(path)) {
			days = JsonSerializer.Deserialize<Times[]>(File.OpenRead(path), new JsonSerializerOptions() {IncludeFields = true});
		}

		if (days == null) {
			var url = $"https://www.moonsighting.com/time_json.php?year={requestTime.Year}&tz=UTC&lat={latitude.Text}&lon={longitude.Text}&method=2&both=0&time=0";
			debug("URL " + url);

			var response = new HttpClient().Send(new() {RequestUri = new(url)});
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
				alert = new(window, 0, MessageType.Error, ButtonsType.Close, false, "Server responded with code {0:d}. Request URL is {1}.", response.StatusCode, url);
			}
		}

		if (days == null) {
			alert ??= new(window, 0, MessageType.Error, ButtonsType.Close, false, "An unknown error occurred.");
			enbuffer(() => alert.Present());

			return;
		}

		writeConfiguration();

		var day = days[requestTime.DayOfYear];

		enbuffer(() => {
			string[] keys = {"fajr", "sunrise", "dhuhr", "asr", "maghrib", "isha"};

			for (var i = 0; i < keys.Length; ++i) {
				ref var prayer = ref prayers[i];
				var t = DateTime.Parse((string) day.GetType().GetField(keys[i])!.GetValue(day)!);
				t = requestTime.Add(t.TimeOfDay - requestTime.TimeOfDay).ToLocalTime();

				if (updateToday) prayer.today = t;
				if (!customDate || !updateToday) prayer.target = t;

				var output = format(prayer.target, "%R");
				prayer.value.Text = output[0] == '0' ? output.Substring(1) : output;
			}

			if (customDate) setDate(requestTime);
			else setDate(time = DateTime.Now);

			calendar.Date = requestTime;
			coordinates = (latitude.Text, longitude.Text);

			resetHighlight();
			nextPrayer = 0;
			highlight();
		});
	}
});

public record struct Prayer(string name) {
	public DateTime today;
	public DateTime target;
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

public struct Times {
	public string fajr, sunrise, dhuhr, asr, maghrib, isha;
	public string? asr_s, asr_h;
}
