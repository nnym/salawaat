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

ApplicationWindow window = new(application) {Title = NAME, DefaultSize = new(320, 220)};

Box top = new(Orientation.Vertical, 20);
window.Child = top;

var customDate = false;
var time = DateTime.Now;

Label date = new("");
Grid table = new() {Halign = Align.Center, RowSpacing = 8, ColumnSpacing = 8};
Expander settingExpander = new("settings") {Halign = Align.Center};

top.Add(date);
top.Add(table);
top.Add(settingExpander);

Box settingBox = new(Orientation.Vertical, 20) {Halign = Align.Center};
var height = window.AllocatedHeight;

settingExpander.Activated += (_, _) => {
	if (settingBox.Visible = settingExpander.Expanded) {
		settingBox.ShowAll();
		height = window.AllocatedHeight;
	} else {
		window.Resize(window.AllocatedWidth, height);
	}
};

Grid settings = new() {MarginTop = 8, RowSpacing = 8, ColumnSpacing = 8};
Button refresh = new("refresh") {CanDefault = true};
window.Default = refresh;

var times = new Label[6];
string[] names = {"fajr", "shuruq", "dhuhr", "'asr", "maghrib", "'isha"};

for (var i = 0; i < names.Length; ++i) {
	table.Attach(new Label(names[i]) {Halign = Align.Start}, 0, i, 1, 1);
	table.Attach(times[i] = new("--:--"), 1, i, 1, 1);
}

EntryBuffer settingRow(int row, string name, int maxLength, EntryBuffer buffer) {
	settings.Attach(new Label(name) {Halign = Align.Start}, 0, row, 1, 1);
	settings.Attach(new Entry(buffer) {ActivatesDefault = true, MaxLength = maxLength}, 1, row, 1, 1);

	return buffer;
}

var latitude = settingRow(0, "latitude", 20, new("", -1));
var longitude = settingRow(1, "longitude", 20, new("", -1));
Calendar calendar = new();

calendar.DaySelected += (_, _) => markToday();
calendar.DaySelectedDoubleClick += (_, _) => refresh.Click();

settingBox.Add(settings);
settingBox.Add(calendar);
settingBox.Add(refresh);

window.ShowAll();
top.Add(settingBox);

refresh.Clicked += (_, _) => {
	customDate = !sameDay(calendar.Date, DateTime.Now);
	load();
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

void markToday() {
	calendar.ClearMarks();
	if (calendar.Month + 1 == time.Month && calendar.Year == time.Year) calendar.MarkDay((uint) DateTime.Now.Day);
}

void tick(uint delay) => GLib.Timeout.Add(delay, () => {
	if (!customDate) {
		var now = DateTime.Now;

		if (!sameDay(now, time)) {
			calendar.Date = now.Date;
			if (now.Second % 15 == 0) load(false);
		} else setDate(time = now);
	}

	tick((uint) (1000 - DateTime.Now.Microsecond / 1000));
	return false;
});

void load(bool loadConfiguration = false) => Task.Run(() => {
	lock (window) {
		Task enbuffer(Action action) {
			Task task = new(action);

			GLib.Idle.Add(() => {
				task.RunSynchronously();
				return false;
			});

			return task;
		}

		if (loadConfiguration) {
			debug("Loading configuration.");

			if (File.Exists(config)) {
				var contents = File.ReadAllLines(config).Where(line => line.Length > 0).ToArray();
				if (contents is [var tz, ..] && !double.TryParse(tz, out _)) contents = contents.Skip(1).ToArray();

				enbuffer(() => {
					if (contents.Length >= 1) latitude.Text = contents[0];
					if (contents.Length >= 2) longitude.Text = contents[1];
					if (contents.Length != 2) warning("Configuration file is corrupt.");
				}).Wait();
			}
		}

		if (new[]{latitude, longitude}.Any(field => field.Text.Length == 0)) {
			enbuffer(() => settingExpander.Expanded = true);
			return;
		}

		DateTimeOffset requestTime = calendar.Date;
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
			if (alert == null) alert = new(window, 0, MessageType.Error, ButtonsType.Close, false, "An unknown error occurred.");

			enbuffer(() => alert.Present());
			return;
		}

		File.WriteAllText(config, string.Join('\n', latitude.Text, longitude.Text));

		var today = days[requestTime.DayOfYear];

		enbuffer(() => {
			string[] keys = {"fajr", "sunrise", "dhuhr", "asr", "maghrib", "isha"};

			for (var i = 0; i < keys.Length; ++i) {
				var t = DateTime.Parse((string) today.GetType().GetField(keys[i])!.GetValue(today)!);
				var output = format(requestTime.Add(requestTime.Offset + t.TimeOfDay - requestTime.TimeOfDay), "%R");
				times[i].Text = output[0] == '0' ? output.Substring(1) : output;
			}

			if (customDate) setDate(requestTime.Date);
			else setDate(time = DateTime.Now);
		});
	}
});

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
