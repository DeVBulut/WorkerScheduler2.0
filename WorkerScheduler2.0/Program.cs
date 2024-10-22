using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System.Threading;
using System.Globalization;

namespace WorkerScheduler
{
    class Program
    {
        static string[] Scopes = { CalendarService.Scope.Calendar }; // Scope for Google Calendar API
        static string ApplicationName = "Worker Scheduler";
        static CalendarService calendarService;
        static string calendarId = "primary"; // Use 'primary' for the main calendar or specify custom calendar ID

        static void Main(string[] args)
        {
            InitializeGoogleCalendar();

            var workers = new List<Worker>();
            Console.WriteLine("Welcome to Worker Scheduler!");

            // Option to input workers via console or use test data
            Console.WriteLine("Do you want to use test data? (yes/no): ");
            string useTestData = Console.ReadLine().ToLower();
            if (useTestData == "yes")
            {
                workers = GetTestWorkers();
            }
            else
            {
                // Input worker details
                Console.WriteLine("Enter worker details (Type 'done' to finish):");
                while (true)
                {
                    Console.Write("Worker Name: ");
                    string name = Console.ReadLine();
                    if (name.ToLower() == "done")
                    {
                        break;
                    }

                    Console.WriteLine("Enter availability details (Type 'done' to finish for this worker):");
                    var availability = new List<Availability>();
                    while (true)
                    {
                        Console.Write("Day (e.g., Mon, Tue, Wed or 'done'): ");
                        string day = Console.ReadLine();
                        if (day.ToLower() == "done")
                        {
                            break;
                        }

                        Console.WriteLine("Enter available hours for this day (Type 'done' to finish for this day):");
                        var availableHoursList = new List<string>();
                        while (true)
                        {
                            Console.Write("Available Hours (e.g., 9-10 or 'done'): ");
                            string availableHours = Console.ReadLine();
                            if (availableHours.ToLower() == "done")
                            {
                                break;
                            }
                            availableHoursList.Add(availableHours);
                        }

                        string combinedHours = string.Join(", ", availableHoursList);
                        availability.Add(new Availability(day, combinedHours));
                    }

                    workers.Add(new Worker(name, availability));
                }
            }

            // Create schedule
            var schedule = GenerateSchedule(workers);

            // Display the schedule
            Console.WriteLine("\nGenerated Schedule:");
            foreach (var entry in schedule)
            {
                Console.WriteLine($"{entry.Day}: {entry.WorkerName} from {entry.Hours}");
            }

            // Save schedule to a local file
            SaveScheduleToFile(schedule, workers);
            Console.WriteLine("\nSchedule saved to 'C:\\C# Projects\\schedule.txt'");

            // Add schedule to Google Calendar
            AddScheduleToGoogleCalendar(schedule);
        }

        static void InitializeGoogleCalendar()
        {
            UserCredential credential;

            using (var stream = new FileStream("credentials.json", FileMode.Open, FileAccess.Read))
            {
                string credPath = "token.json";
                credential = GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.Load(stream).Secrets,
                    Scopes,
                    "user",
                    CancellationToken.None,
                    new FileDataStore(credPath, true)).Result;
                Console.WriteLine("Credential file saved to: " + credPath);
            }

            // Creating Google Calendar API service.
            calendarService = new CalendarService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });

            CreateCustomCalendar();
        }

        static void CreateCustomCalendar()
        {
            var calendar = new Google.Apis.Calendar.v3.Data.Calendar();
            calendar.Summary = "Worker Schedule Calendar";
            calendar.TimeZone = "America/Los_Angeles";

            var createdCalendar = calendarService.Calendars.Insert(calendar).Execute();
            calendarId = createdCalendar.Id;

            Console.WriteLine("Custom Calendar created with ID: " + calendarId);
        }

        static void AddScheduleToGoogleCalendar(List<ScheduleEntry> schedule)
        {
            foreach (var entry in schedule)
            {
                if (entry.WorkerName == "No Worker Available" || entry.WorkerName == "Not Enough Workers")
                    continue;

                var eventToAdd = new Event()
                {
                    Summary = $"Shift for {entry.WorkerName}",
                    Start = new EventDateTime()
                    {
                        DateTime = ConvertToDateTime(entry.Day, entry.Hours.Split('-')[0]),
                        TimeZone = "America/Los_Angeles",
                    },
                    End = new EventDateTime()
                    {
                        DateTime = ConvertToDateTime(entry.Day, entry.Hours.Split('-')[1]),
                        TimeZone = "America/Los_Angeles",
                    },
                };

                calendarService.Events.Insert(eventToAdd, calendarId).Execute();
            }

            Console.WriteLine("Schedule successfully added to Google Calendar.");
        }

        static DateTime ConvertToDateTime(string day, string hour)
        {
            int dayOfWeek = day switch
            {
                "Mon" => 1,
                "Tue" => 2,
                "Wed" => 3,
                "Thu" => 4,
                "Fri" => 5,
                "Sun" => 0,
                _ => throw new Exception("Invalid day of the week"),
            };

            DateTime now = DateTime.Now;
            DateTime targetDay = now.AddDays((dayOfWeek - (int)now.DayOfWeek + 7) % 7);
            return new DateTime(targetDay.Year, targetDay.Month, targetDay.Day, int.Parse(hour), 0, 0);
        }

        static List<ScheduleEntry> GenerateSchedule(List<Worker> workers)
        {
            var schedule = new List<ScheduleEntry>();
            var daysOfWeek = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sun" }; 
            var workerHours = new Dictionary<string, int>();

            foreach (var day in daysOfWeek)
            {
                var availableWorkers = workers.Where(w => w.Availability.Any(a => a.Day == day)).ToList();
                availableWorkers = availableWorkers.Where(w => GetTotalHours(workerHours, w.Name) < 20).ToList();

                if (availableWorkers.Any())
                {
                    foreach (var worker in availableWorkers)
                    {
                        if (GetTotalHours(workerHours, worker.Name) >= 20)
                        {
                            continue; // Skip workers who already have 20 hours assigned
                        }

                        var availabilities = worker.Availability.FirstOrDefault(a => a.Day == day)?.AvailableHours.Split(new string[] { ", " }, StringSplitOptions.None);
                        if (availabilities != null)
                        {
                            foreach (var availability in availabilities)
                            {
                                if (GetTotalHours(workerHours, worker.Name) + CalculateHours(availability) <= 20)
                                {
                                    schedule.Add(new ScheduleEntry(day, worker.Name, availability));
                                    workerHours[worker.Name] = GetTotalHours(workerHours, worker.Name) + CalculateHours(availability);
                                }
                            }
                        }
                    }
                }
                else
                {
                    schedule.Add(new ScheduleEntry(day, "No Worker Available", "N/A"));
                }
            }

            return schedule;
        }

        static int GetTotalHours(Dictionary<string, int> workerHours, string workerName)
        {
            return workerHours.ContainsKey(workerName) ? workerHours[workerName] : 0;
        }

        static int CalculateHours(string availableTime)
        {
            var hours = availableTime.Split('-');
            if (hours.Length == 2 && int.TryParse(hours[0], out int start) && int.TryParse(hours[1], out int end))
            {
                return end - start;
            }
            return 0;
        }

        static void SaveScheduleToFile(List<ScheduleEntry> schedule, List<Worker> workers)
        {
            var workerHours = new Dictionary<string, int>();
            foreach (var entry in schedule)
            {
                if (entry.WorkerName != "No Worker Available" && entry.WorkerName != "Not Enough Workers")
                {
                    if (!workerHours.ContainsKey(entry.WorkerName))
                    {
                        workerHours[entry.WorkerName] = 0;
                    }
                    workerHours[entry.WorkerName] += CalculateHours(entry.Hours);
                }
            }

            using (var writer = new StreamWriter("C:\\C# Projects\\schedule.txt"))
            {
                foreach (var entry in schedule)
                {
                    writer.WriteLine($"{entry.Day}: {entry.WorkerName} from {entry.Hours}");
                }

                writer.WriteLine("\nTotal Hours Per Worker:");
                foreach (var worker in workers)
                {
                    var hours = workerHours.ContainsKey(worker.Name) ? workerHours[worker.Name] : 0;
                    writer.WriteLine($"{worker.Name}: {hours} hours");
                }
            }
        }

        static List<Worker> GetTestWorkers()
        {
            return new List<Worker>
            {
                new Worker("John", new List<Availability>
                {
                    new Availability("Mon", "7-9, 13-15"),
                    new Availability("Tue", "9-11, 15-17"),
                    new Availability("Wed", "7-10, 14-16"),
                    new Availability("Thu", "8-10, 12-14, 16-18"),
                    new Availability("Fri", "7-11, 13-15")
                }),
                new Worker("Jane", new List<Availability>
                {
                    new Availability("Mon", "8-11, 14-16"),
                    new Availability("Tue", "7-9, 13-15"),
                    new Availability("Wed", "9-12, 15-18"),
                    new Availability("Thu", "7-10, 13-15"),
                    new Availability("Fri", "8-12, 14-16")
                }),
                new Worker("Alex", new List<Availability>
                {
                    new Availability("Mon", "7-9, 11-13, 15-17"),
                    new Availability("Tue", "9-12, 14-16"),
                    new Availability("Wed", "8-11, 13-15"),
                    new Availability("Thu", "7-8, 10-12, 14-16"),
                    new Availability("Fri", "9-11, 13-15")
                }),
                new Worker("Mike", new List<Availability>
                {
                    new Availability("Mon", "9-12, 14-17"),
                    new Availability("Tue", "7-9, 12-14"),
                    new Availability("Wed", "8-10, 13-15"),
                    new Availability("Thu", "7-11, 15-18"),
                    new Availability("Fri", "10-13, 14-16")
                }),
                new Worker("Sara", new List<Availability>
                {
                    new Availability("Mon", "7-8, 11-13, 15-18"),
                    new Availability("Tue", "9-11, 14-16"),
                    new Availability("Wed", "8-10, 13-15"),
                    new Availability("Thu", "7-9, 12-15"),
                    new Availability("Fri", "9-12, 14-17")
                }),
                new Worker("Tom", new List<Availability>
                {
                    new Availability("Mon", "7-11, 14-17"),
                    new Availability("Tue", "8-10, 13-15"),
                    new Availability("Wed", "7-9, 12-15"),
                    new Availability("Thu", "10-12, 15-17"),
                    new Availability("Fri", "7-8, 11-14, 16-18")
                }),
                new Worker("Lucy", new List<Availability>
                {
                    new Availability("Mon", "8-10, 12-14, 16-18"),
                    new Availability("Tue", "7-9, 10-12, 15-17"),
                    new Availability("Wed", "9-11, 13-16"),
                    new Availability("Thu", "7-10, 14-16"),
                    new Availability("Fri", "8-11, 12-15")
                }),
                new Worker("Emma", new List<Availability>
                {
                    new Availability("Mon", "7-9, 11-13, 15-18"),
                    new Availability("Tue", "8-10, 13-15"),
                    new Availability("Wed", "7-8, 12-15"),
                    new Availability("Thu", "9-11, 13-16"),
                    new Availability("Fri", "8-10, 14-17")
                }),
                new Worker("Ryan", new List<Availability>
                {
                    new Availability("Mon", "7-10, 13-15"),
                    new Availability("Tue", "8-9, 12-14, 16-18"),
                    new Availability("Wed", "9-12, 15-17"),
                    new Availability("Thu", "7-8, 10-12, 13-15"),
                    new Availability("Fri", "8-11, 14-16")
                }),
                new Worker("Olivia", new List<Availability>
                {
                    new Availability("Mon", "8-10, 12-15"),
                    new Availability("Tue", "7-9, 11-14, 16-18"),
                    new Availability("Wed", "9-11, 13-15"),
                    new Availability("Thu", "7-10, 12-14, 15-17"),
                    new Availability("Fri", "8-9, 11-13, 14-16")
                }),
                new Worker("Noah", new List<Availability>
                {
                    new Availability("Mon", "7-8, 10-13, 15-18"),
                    new Availability("Tue", "8-11, 14-16"),
                    new Availability("Wed", "9-12, 13-15"),
                    new Availability("Thu", "7-9, 12-15"),
                    new Availability("Fri", "10-13, 14-16")
                }),
                new Worker("Liam", new List<Availability>
                {
                    new Availability("Mon", "9-11, 14-16"),
                    new Availability("Tue", "7-8, 12-14, 15-17"),
                    new Availability("Wed", "8-10, 13-15"),
                    new Availability("Thu", "7-9, 10-12, 14-16"),
                    new Availability("Fri", "9-11, 13-15")
                }),
                new Worker("Sophia", new List<Availability>
                {
                    new Availability("Mon", "7-9, 11-13, 14-16"),
                    new Availability("Tue", "9-12, 15-18"),
                    new Availability("Wed", "8-10, 12-15"),
                    new Availability("Thu", "7-8, 10-13, 14-17"),
                    new Availability("Fri", "9-11, 13-16")
                }),
                new Worker("James", new List<Availability>
                {
                    new Availability("Mon", "8-11, 13-15"),
                    new Availability("Tue", "7-9, 11-13, 15-17"),
                    new Availability("Wed", "9-10, 12-14, 16-18"),
                    new Availability("Thu", "7-8, 10-12, 13-15"),
                    new Availability("Fri", "8-11, 14-16")
                }),
                new Worker("Ethan", new List<Availability>
                {
                    new Availability("Mon", "7-9, 12-14"),
                    new Availability("Tue", "8-10, 13-15"),
                    new Availability("Wed", "7-8, 11-13, 15-17"),
                    new Availability("Thu", "9-12, 14-16"),
                    new Availability("Fri", "8-9, 11-13, 14-16")
                }),
                new Worker("Charlotte", new List<Availability>
                {
                    new Availability("Mon", "7-8, 10-12, 14-17"),
                    new Availability("Tue", "8-11, 15-18"),
                    new Availability("Wed", "9-11, 13-15"),
                    new Availability("Thu", "7-10, 12-14, 16-18"),
                    new Availability("Fri", "8-10, 13-16")
                }),
                new Worker("Jack", new List<Availability>
                {
                    new Availability("Mon", "8-10, 12-15"),
                    new Availability("Tue", "7-9, 11-13, 15-17"),
                    new Availability("Wed", "9-12, 14-16"),
                    new Availability("Thu", "8-10, 13-15, 17-18"),
                    new Availability("Fri", "7-8, 10-13, 14-16")
                }),
                new Worker("Mia", new List<Availability>
                {
                    new Availability("Mon", "7-9, 11-13"),
                    new Availability("Tue", "8-12, 14-17"),
                    new Availability("Wed", "9-11, 13-16"),
                    new Availability("Thu", "7-8, 10-12, 15-17"),
                    new Availability("Fri", "8-9, 11-13, 14-16")
                }),
                new Worker("Harper", new List<Availability>
                {
                    new Availability("Mon", "7-8, 10-12, 14-18"),
                    new Availability("Tue", "8-10, 13-16"),
                    new Availability("Wed", "7-9, 11-13, 15-17"),
                    new Availability("Thu", "9-11, 13-15, 16-18"),
                    new Availability("Fri", "8-11, 14-16")
                }),
                new Worker("Mason", new List<Availability>
                {
                    new Availability("Mon", "7-9, 12-14"),
                    new Availability("Tue", "8-11, 15-17"),
                    new Availability("Wed", "9-10, 13-15"),
                    new Availability("Thu", "8-10, 12-14, 16-18"),
                    new Availability("Fri", "7-8, 10-13")
                }),
                new Worker("Aiden", new List<Availability>
                {
                    new Availability("Mon", "8-10, 13-15"),
                    new Availability("Tue", "7-9, 11-14"),
                    new Availability("Wed", "8-10, 12-14, 15-17"),
                    new Availability("Thu", "9-11, 14-16"),
                    new Availability("Fri", "8-11, 13-15")
                })
            };
        }
    }

    class Worker
    {
        public string Name { get; set; }
        public List<Availability> Availability { get; set; }

        public Worker(string name, List<Availability> availability)
        {
            Name = name;
            Availability = availability;
        }
    }

    class Availability
    {
        public string Day { get; set; }
        public string AvailableHours { get; set; }

        public Availability(string day, string availableHours)
        {
            Day = day;
            AvailableHours = availableHours;
        }
    }

    class ScheduleEntry
    {
        public string Day { get; set; }
        public string WorkerName { get; set; }
        public string Hours { get; set; }

        public ScheduleEntry(string day, string workerName, string hours)
        {
            Day = day;
            WorkerName = workerName;
            Hours = hours;
        }
    }
}
