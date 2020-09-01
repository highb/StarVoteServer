﻿using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Google.Apis.Util;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StarVoteServer.GoogleFunctions
{
    public class GoogleService : ServiceAccount
    {
        public GoogleService(string spreadsheetId) : base(spreadsheetId) { }
        public const string StarSymbol = "\u2605";

        public async Task<string> ReadRange(string range)
        {
            SpreadsheetsResource.ValuesResource.GetRequest request = _service.Spreadsheets.Values.Get(_spreadsheetId, range);
            //request.ValueRenderOption = valueRenderOption;

            var response = await request.ExecuteAsync().ConfigureAwait(false);
            return JsonConvert.SerializeObject(response.Values);
        }

        public async Task<string> WriteRange(string range, IList<IList<object>> values)
        {
            var request = BuildUpdateRequest(range, values);
            var response = await request.ExecuteAsync().ConfigureAwait(false);
            return JsonConvert.SerializeObject(response.UpdatedCells);
        }

        public async Task<GoogleSheetInfo> GetSheetInfo()
        {
            var request = _service.Spreadsheets.Get(_spreadsheetId);
            var response = await request.ExecuteAsync().ConfigureAwait(false);
            var info = new GoogleSheetInfo
            {
                Title = response.Properties.Title,
                TimeZone = response.Properties.TimeZone
            };
            foreach (var sheet in response.Sheets)
            {
                var props = sheet.Properties;
                info.Sheets.Add(new SheetInfo { Index = props.Index.GetValueOrDefault(-1), SheetId = props.SheetId.GetValueOrDefault(-1), Title = props.Title });
            }
            return info;
        }


        public async Task<string> CreateSheet()
        {
            base.BeginBatch();
            base.AddNewSheet(StarSymbol + "Voters", 1, "VoterId", "Timestamp", "Email");

            var formatRequest = new RepeatCellRequest
            {
                Range = new GridRange { SheetId = 1, StartColumnIndex = 1, EndColumnIndex = 2, StartRowIndex = 0 },
                Cell = new CellData { UserEnteredFormat = new CellFormat { NumberFormat = new NumberFormat { Type = "DATE", Pattern = "M/d/yy hh:mm" } } },
                Fields = "userEnteredFormat.numberFormat"
            };

            AddToBatch(new Request { RepeatCell = formatRequest });
            var batch = EndBatch();
            var response = await batch.ExecuteAsync().ConfigureAwait(false);
            return JsonConvert.SerializeObject(response);
        }

        internal async Task<Election> GetElection(SheetInfo settingsSheet, SheetInfo votersSheet, List<SheetInfo> raceSheets)
        {
            var ranges = new List<string>();
            ranges.Add($"{settingsSheet.Title}!A:B");
            ranges.Add($"{votersSheet.Title}!A:A");
            foreach(var race in raceSheets)
            {
                ranges.Add($"{race.Title}!1:1");
            }
            var request = _service.Spreadsheets.Values.BatchGet(_spreadsheetId);
            request.Ranges = new Repeatable<string>(ranges);
            var response = await request.ExecuteAsync().ConfigureAwait(false);

            var sheets = response.ValueRanges.ToArray();
            var election = new Election();
            election.Settings = ParseSettings(sheets[0]);
            election.AuthorizedVoters = ParseVoters(sheets[1]);
            election.Races = ParseRaces(raceSheets, sheets.Slice(2));

            return election;
        }

        private List<Race> ParseRaces(List<SheetInfo> raceSheets, ValueRange[] ranges)
        {
            var list = new List<Race>();
            for (var i = 0; i < raceSheets.Count; i++)
            {
                var range = ranges[i].Values.First<IList<object>>();
                var caption = raceSheets[i].Title.Substring(1); 
                var candidates = new List<string>();
                var columns = range.ToArray<object>().Slice<object>(1);
                foreach(var column in columns)
                {
                    candidates.Add(column.ToString());
                }
                var race = new Race
                {
                    Caption = caption,
                    Candidates = candidates
                };
                list.Add(race);
            }
            return list;
        }

        private List<string> ParseVoters(ValueRange range)
        {
            var validated = false;
            var list = new List<string>();
            foreach (var row in range.Values)
            {
                if (!validated)
                {
                    validated = true;
                    continue;
                }
                var columns = row.ToArray<object>();
                var email = columns[0].ToString().ToLower();
                list.Add(email);
            }
            return list;
        }

        private ElectionSettings ParseSettings(ValueRange range)
        {
            var settings = new ElectionSettings();
            foreach (var row in range.Values)
            {
                var columns = row.ToArray<object>();
                var name = columns[0].ToString().ToLower();
                var value = columns.Length > 1 ? columns[1].ToString() : "";
                switch (name)
                {
                    case "starttime":
                        DateTime.TryParse(value, out var startTime);
                        settings.StartTime = startTime;
                        break;
                    case "endtime":
                        DateTime.TryParse(value, out var endTime);
                        settings.EndTime = endTime;
                        break;
                    case "infourl":
                        settings.InfoUrl = value;
                        break;
                    case "adminemail":
                        settings.AdminEmail = value;
                        break;
                    case "supportemail":
                        settings.SupportEmail = value;
                        break;
                    case "auditemail":
                        settings.AuditEmail = value;
                        break;
                    case "emailverification":
                        settings.EmailVerification = ParseBoolean(value);
                        break;
                    case "voterauthorization":
                        settings.VoterAuthorization = ParseBoolean(value);
                        break;
                    case "ballotupdates":
                        settings.BallotUpdates = ParseBoolean(value);
                        break;
                    case "publicresults":
                        settings.PublicResults = ParseBoolean(value);
                        break;
                    default:
                        break;
                }
            }
            return settings;
        }

        private bool? ParseBoolean(string value)
        {
            var booleanValue = value.ToLower();
            if (booleanValue == "true") return true;
            if (booleanValue == "false") return false;
            return null;
        }

        public async Task<string> Initialize(Election election)
        {
            base.BeginBatch();

            // First, let's add sheets for each of the races
            var sheetId = 2;
            foreach (var race in election.Races.Reverse<Race>())
            // NOTE: There is some sort of bug with the Google Sheets API that shows up
            // when adding multiple sheets in a single batch.  Rather than just adding
            // each sheet at the end, like the suggests should happen, they end up in
            // a different order.  In order to work around that bug, the AddNewSheet method
            // is set up to insert each sheet in the second position.  So, to get the
            // races in the order we want, we add them in reverse order.
            {
                var columns = new List<string> { "VoterId" };
                columns.AddRange(race.Candidates);
                AddNewSheet(StarSymbol + race.Caption, sheetId, columns.ToArray());
                AddCentering(new GridRange { SheetId = sheetId, StartColumnIndex = 1, StartRowIndex = 0, EndColumnIndex = columns.Count });
                sheetId++;
            }

            base.AddToBatch(new Request
            {
                UpdateSpreadsheetProperties = new UpdateSpreadsheetPropertiesRequest
                {
                    Properties = new SpreadsheetProperties
                    {
                        Title = election.Title
                    },
                    Fields="title"
                }
            }); ; ; ;
            // Clear any data on the default sheet
            base.AddToBatch(new Request
            {
                UpdateCells = new UpdateCellsRequest
                {
                    Fields = "*",
                    Range = new GridRange { SheetId = 0 }
                }
            });
            // Then replace it with the proper settings
            base.AddToBatch(new Request
            {
                UpdateCells = new UpdateCellsRequest
                {
                    Range = new GridRange
                    {
                        SheetId = 0,
                        StartColumnIndex = 0,
                        StartRowIndex = 0,
                        EndColumnIndex = 3,
                        EndRowIndex = 100
                    },
                    Rows = election.Settings.ToRowData(),
                    Fields = "userEnteredValue"
                }
            });
            // Add apply date formatting to the date fields
            base.AddDateFormating(new GridRange {
                SheetId = 0,
                StartColumnIndex = 1,
                EndColumnIndex = 2,
                StartRowIndex = 0,
                EndRowIndex = 2
            });
            // Finally, rename the default sheet
            base.AddToBatch(new Request
            {
                UpdateSheetProperties = new UpdateSheetPropertiesRequest
                {
                    Properties = new SheetProperties { Title = StarSymbol + "Settings" },
                    Fields = "title"
                }
            });

            // Next, add a second sheet for recording who voted
            AddNewSheet(StarSymbol + "Voters", 1, "Email", "VoterId", "Timestamp");

            // And apply date formatting to the date fields
            base.AddDateFormating(new GridRange
            {
                SheetId = 1,
                StartColumnIndex = 2,
                EndColumnIndex = 3,
                StartRowIndex = 0
            });

            // If there is a list of authorized voters, add it to the Voters tab
            if (election.AuthorizedVoters != null && election.AuthorizedVoters.Count > 0)
            {
                var list = new List<RowData>();
                foreach(var voter in election.AuthorizedVoters)
                {
                    var row = new string[] { voter };
                    list.Add(row.ToRowData());
                }
                AddToBatch(new Request
                {
                    AppendCells = new AppendCellsRequest
                    {
                        SheetId = 1,
                        Rows = list,
                        Fields = "userEnteredValue"
                                            }
                });
            }

            var batch = EndBatch();
            var response = await batch.ExecuteAsync().ConfigureAwait(false);
            return JsonConvert.SerializeObject(response);
        }
    }
}