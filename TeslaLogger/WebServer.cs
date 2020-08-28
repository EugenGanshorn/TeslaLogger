﻿using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Caching;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace TeslaLogger
{
    public class WebServer
    {
        private HttpListener listener = null;

        public WebServer()
        {
            if (!HttpListener.IsSupported)
            {
                Logfile.Log("HttpListener is not Supported!!!");
                return;
            }
            
            try
            {
                int httpport = Tools.GetHttpPort();
                listener = new HttpListener();
                listener.Prefixes.Add($"http://*:{httpport}/");
                listener.Start();

                Logfile.Log($"HttpListener bound to http://*:{httpport}/");
            }
            catch (HttpListenerException hlex)
            {
                listener = null;
                if (((UInt32)hlex.HResult) == 0x80004005)
                {
                    Logfile.Log("HTTPListener access denied. Check https://stackoverflow.com/questions/4019466/httplistener-access-denied");
                }
                else
                {
                    Logfile.Log(hlex.ToString());
                }
            }
            catch (Exception ex)
            {
                listener = null;
                Logfile.Log(ex.ToString());
            }

            try
            {
                if (listener == null)
                {
                    int httpport = Tools.GetHttpPort();
                    listener = new HttpListener();
                    listener.Prefixes.Add($"http://localhost:{httpport}/");
                    listener.Start();

                    Logfile.Log($"HTTPListener only bound to Localhost:{httpport}!");
                }
            }
            catch (HttpListenerException hlex)
            {
                listener = null;
                if (((UInt32)hlex.HResult) == 0x80004005)
                {
                    Logfile.Log("HTTPListener access denied. Check https://stackoverflow.com/questions/4019466/httplistener-access-denied");
                }
                else
                {
                    Logfile.Log(hlex.ToString());
                }
            }
            catch (Exception ex)
            {
                listener = null;
                Logfile.Log(ex.ToString());
            }

            while (true)
            {
                try
                {
                    ThreadPool.QueueUserWorkItem(OnContext, listener.GetContext());
                }
                catch (Exception ex)
                {
                    Logfile.Log(ex.ToString());
                }
            }
        }

        private void OnContext(object o)
        {
            try
            {
                HttpListenerContext context = o as HttpListenerContext;

                HttpListenerRequest request = context.Request;
                HttpListenerResponse response = context.Response;

                switch (true)
                {
                    // commands
                    case bool _ when request.Url.LocalPath.Equals("/getchargingstate"):
                        Getchargingstate(request, response);
                        break;
                    case bool _ when request.Url.LocalPath.Equals("/setcost"):
                        Setcost(request, response);
                        break;
                    case bool _ when request.Url.LocalPath.Equals("/getallcars"):
                        GetAllCars(request, response);
                        break;
                    // car values
                    case bool _ when Regex.IsMatch(request.Url.LocalPath, @"/get/[0-9]+/.+"):
                        Get_CarValue(request, response);
                        break;
                    case bool _ when request.Url.LocalPath.Equals("/admin/UpdateElevation"):
                        Admin_UpdateElevation(request, response);
                        break;
                    case bool _ when request.Url.LocalPath.Equals("/admin/ReloadGeofence"):
                        // optional query parameter: html --> returns html instead of JSON
                        Admin_ReloadGeofence(request, response);
                        break;
                    // Tesla API debug
                    case bool _ when Regex.IsMatch(request.Url.LocalPath, @"/debug/TeslaAPI/[0-9]+/.+"):
                        Debug_TeslaAPI(request.Url.LocalPath, request, response);
                        break;
                    case bool _ when Regex.IsMatch(request.Url.LocalPath, @"/debug/TeslaLogger/[0-9]+/states"):
                        Debug_TeslaLoggerStates(request, response);
                        break;
                    default:
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        WriteString(response, @"URL Not Found!");
                        break;
                }

            }
            catch (Exception ex)
            {
                Logfile.Log(ex.ToString());
            }
        }

        private void Get_CarValue(HttpListenerRequest request, HttpListenerResponse response)
        {
            Match m = Regex.Match(request.Url.LocalPath, @"/get/([0-9]+)/(.+)");
            if (m.Success && m.Groups.Count == 3 && m.Groups[1].Captures.Count == 1 && m.Groups[2].Captures.Count == 1)
            {
                string value = m.Groups[1].Captures[0].ToString();
                int.TryParse(m.Groups[2].Captures[0].ToString(), out int CarID);
                if (value.Length > 0 && CarID > 0)
                {
                    Car car = Car.GetCarByID(CarID);
                    if (car != null)
                    {
                        if (car.currentJSON.GetType().GetProperty(value) != null)
                        {
                            object val = car.currentJSON.GetType().GetProperty(value).GetValue(car.currentJSON);
                            Logfile.Log($"GetCarValue: {request.Url.LocalPath} - {value} - {CarID} -- {val}");
                            if (request.QueryString.Count == 1 && string.Concat(request.QueryString.GetValues(0)).Equals("raw"))
                            {
                                WriteString(response, val.ToString());
                            }
                            else
                            {
                                // TODO return JSON
                            }
                        }
                    }
                }
            }
        }

        private static void Admin_ReloadGeofence(HttpListenerRequest request, HttpListenerResponse response)
        {
            Logfile.Log("Admin: ReloadGeofence ...");
            WebHelper.geofence.Init();
            
            if (request.QueryString.Count == 1 && string.Concat(request.QueryString.GetValues(0)).Equals("html"))
            {
                IEnumerable<string> trs = WebHelper.geofence.sortedList.Select(
                    a => string.Format("<tr><td>{0}</td><td>{1}</td><td>{2}</td><td>{3}</td><td>{4}</td><td>{5}</td></tr>",
                        a.name,
                        a.lat,
                        a.lng, 
                        a.radius,
                        string.Concat(a.specialFlags.Select(
                            sp => string.Format("{0}<br/>",
                            sp.ToString()))
                        ),
                        a.geofenceSource.ToString()
                    )
                );
                WriteString(response, "<html><head></head><body><table border=\"1\">" + string.Concat(trs) + "</table></body></html>");
            }
            else
            {
                // TODO return JSON response success/error message like Tesla API
                WriteString(response, "Admin: ReloadGeofence ...");
            }
            WebHelper.UpdateAllPOIAddresses();
            Logfile.Log("Admin: ReloadGeofence done");
        }

        private void Debug_TeslaLoggerStates(HttpListenerRequest request, HttpListenerResponse response)
        {
            /* TODO
            Dictionary<string, string> values = new Dictionary<string, string>
            {
                { "System.DateTime.Now", DateTime.Now.ToString() },
                { "System.DateTime.UtcNow", DateTime.UtcNow.ToString() },
                { "System.DateTime.UnixTime", Tools.ToUnixTime(DateTime.Now).ToString() },
                { "Program._currentState", Program.GetCurrentState().ToString() },
                { "WebHelper._lastShift_State", Program.GetWebHelper().GetLastShiftState() },
                { "Program.highFreequencyLogging", Program.GetHighFreequencyLogging().ToString() },
                { "Program.highFrequencyLoggingTicks", Program.GetHighFrequencyLoggingTicks().ToString() },
                { "Program.highFrequencyLoggingTicksLimit", Program.GetHighFrequencyLoggingTicksLimit().ToString() },
                { "Program.highFrequencyLoggingUntil", Program.GetHighFrequencyLoggingUntil().ToString() },
                { "Program.highFrequencyLoggingMode", Program.GetHighFrequencyLoggingMode().ToString() },
                {
                    "TLMemCacheKey.GetOutsideTempAsync",
                    MemoryCache.Default.Get(Program.TLMemCacheKey.GetOutsideTempAsync.ToString()) != null
                        ? ((double)MemoryCache.Default.Get(Program.TLMemCacheKey.GetOutsideTempAsync.ToString())).ToString()
                        : "null"
                },
                {
                    "TLMemCacheKey.Housekeeping",
                    MemoryCache.Default.Get(Program.TLMemCacheKey.Housekeeping.ToString()) != null
                        ? "AbsoluteExpiration: " + ((CacheItemPolicy)MemoryCache.Default.Get(Program.TLMemCacheKey.Housekeeping.ToString())).AbsoluteExpiration.ToString()
                        : "null"
                },
                { "Program.lastCarUsed", Program.GetLastCarUsed().ToString() },
                { "Program.lastOdometerChanged", Program.GetLastOdometerChanged().ToString() },
                { "Program.lastTryTokenRefresh", Program.GetLastTryTokenRefresh().ToString() },
                {
                    "Program.lastSetChargeLimitAddressName",
                    Program.GetLastSetChargeLimitAddressName().Equals(string.Empty)
                        ? "&lt;&gt;"
                        : Program.GetLastSetChargeLimitAddressName()
                },
                { "Program.goSleepWithWakeup", Program.GetGoSleepWithWakeup().ToString() },
                { "Program.odometerLastTrip", Program.GetOdometerLastTrip().ToString() },
                { "WebHelper.lastIsDriveTimestamp", Program.GetWebHelper().lastIsDriveTimestamp.ToString() },
                { "WebHelper.lastUpdateEfficiency", Program.GetWebHelper().lastUpdateEfficiency.ToString() },
                { "UpdateTeslalogger.lastVersionCheck", UpdateTeslalogger.GetLastVersionCheck().ToString() }
            };
            IEnumerable<string> trs = values.Select(a => string.Format("<tr><td>{0}</td><td>{1}</td></tr>", a.Key, a.Value));
            WriteString(response, "<html><head></head><body><table>" + string.Concat(trs) + "</table></body></html>");
            
             */
        }

        private void Debug_TeslaAPI(string path, HttpListenerRequest request, HttpListenerResponse response)
        {
            int position = path.LastIndexOf('/');
            if (position > -1)
            {
                path = path.Substring(position + 1);
                if (path.Length > 0 && WebHelper.TeslaAPI_Commands.TryGetValue(path, out string TeslaAPIJSON))
                {
                    response.AddHeader("Content-Type", "application/json");
                    WriteString(response, TeslaAPIJSON);
                }
            }
        }

        private void Setcost(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                Logfile.Log("SetCost");

                string json;

                if (request.QueryString["JSON"] != null)
                {
                    json = request.QueryString["JSON"];
                }
                else
                {
                    using (StreamReader reader = new StreamReader(request.InputStream, request.ContentEncoding))
                    {
                        json = reader.ReadToEnd();
                    }
                }

                Logfile.Log("JSON: " + json);

                dynamic j = new JavaScriptSerializer().DeserializeObject(json);

                using (MySqlConnection con = new MySqlConnection(DBHelper.DBConnectionstring))
                {
                    con.Open();
                    MySqlCommand cmd = new MySqlCommand("update chargingstate set cost_total = @cost_total, cost_currency=@cost_currency, cost_per_kwh=@cost_per_kwh, cost_per_session=@cost_per_session, cost_per_minute=@cost_per_minute, cost_idle_fee_total=@cost_idle_fee_total, cost_kwh_meter_invoice=@cost_kwh_meter_invoice  where id= @id", con);

                    if (DBHelper.DBNullIfEmptyOrZero(j["cost_total"]) is DBNull && DBHelper.IsZero(j["cost_per_session"]))
                    {
                        cmd.Parameters.AddWithValue("@cost_total", 0);
                    }
                    else
                    {
                        cmd.Parameters.AddWithValue("@cost_total", DBHelper.DBNullIfEmptyOrZero(j["cost_total"]));
                    }

                    cmd.Parameters.AddWithValue("@cost_currency", DBHelper.DBNullIfEmpty(j["cost_currency"]));
                    cmd.Parameters.AddWithValue("@cost_per_kwh", DBHelper.DBNullIfEmpty(j["cost_per_kwh"]));
                    cmd.Parameters.AddWithValue("@cost_per_session", DBHelper.DBNullIfEmpty(j["cost_per_session"]));
                    cmd.Parameters.AddWithValue("@cost_per_minute", DBHelper.DBNullIfEmpty(j["cost_per_minute"]));
                    cmd.Parameters.AddWithValue("@cost_idle_fee_total", DBHelper.DBNullIfEmpty(j["cost_idle_fee_total"]));
                    cmd.Parameters.AddWithValue("@cost_kwh_meter_invoice", DBHelper.DBNullIfEmpty(j["cost_kwh_meter_invoice"]));

                    cmd.Parameters.AddWithValue("@id", j["id"]);
                    int done = cmd.ExecuteNonQuery();

                    Logfile.Log("SetCost OK: " + done);
                    WriteString(response, "OK");
                }
            }
            catch (Exception ex)
            {
                Logfile.Log(ex.ToString());
                WriteString(response, "ERROR");
            }
        }

        private void Getchargingstate(HttpListenerRequest request, HttpListenerResponse response)
        {
            string id = request.QueryString["id"];
            string responseString = "";

            try
            {
                Logfile.Log("HTTP getchargingstate");                
                DataTable dt = new DataTable();
                MySqlDataAdapter da = new MySqlDataAdapter("SELECT chargingstate.*, lat, lng, address, charging.charge_energy_added as kWh FROM chargingstate join pos on chargingstate.pos = pos.id join charging on chargingstate.EndChargingID = charging.id where chargingstate.id = @id", DBHelper.DBConnectionstring);
                da.SelectCommand.Parameters.AddWithValue("@id", id);
                da.Fill(dt);

                responseString = dt.Rows.Count > 0 ? Tools.DataTableToJSONWithJavaScriptSerializer(dt) : "not found!";
            }
            catch (Exception ex)
            {
                Logfile.Log(ex.ToString());
            }

            WriteString(response, responseString);
        }

        private void GetAllCars(HttpListenerRequest request, HttpListenerResponse response)
        {
            string responseString = "";

            try
            {
                Logfile.Log("HTTP GetAllCars");
                DataTable dt = new DataTable();
                MySqlDataAdapter da = new MySqlDataAdapter("SELECT id, display_name, tasker_hash FROM cars order by display_name", DBHelper.DBConnectionstring);
                da.Fill(dt);

                responseString = dt.Rows.Count > 0 ? Tools.DataTableToJSONWithJavaScriptSerializer(dt) : "not found!";
            }
            catch (Exception ex)
            {
                Logfile.Log(ex.ToString());
            }

            WriteString(response, responseString);
        }

        private static void WriteString(HttpListenerResponse response, string responseString)
        {
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
            // Get a response stream and write the response to it.
            response.ContentLength64 = buffer.Length;
            Stream output = response.OutputStream;
            output.Write(buffer, 0, buffer.Length);
            // You must close the output stream.
            output.Close();
        }

        private void Admin_UpdateElevation(HttpListenerRequest request, HttpListenerResponse response)
        {
            /* TODO
             
            int from = 1;
            int to = DBHelper.GetMaxPosid(this);
            Logfile.Log($"Admin: UpdateElevation ({from} -> {to}) ...");
            WriteString(response, $"Admin: UpdateElevation ({from} -> {to}) ...");
            DBHelper.UpdateTripElevation(from, to);
            Logfile.Log("Admin: UpdateElevation done");
            */
        }
    }
}