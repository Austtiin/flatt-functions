using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace flatt_functions
{
    public class DecodeVin
    {
        private readonly ILogger<DecodeVin> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public DecodeVin(ILogger<DecodeVin> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        [Function("DecodeVin")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "decodevins/{vin?}")] HttpRequestData req,
            string? vin)
        {
            var response = req.CreateResponse();
            
            // Add CORS headers
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            
            try
            {
                // Handle OPTIONS request
                if (req.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = HttpStatusCode.OK;
                    return response;
                }

                // Get VIN from route or query string
                if (string.IsNullOrWhiteSpace(vin))
                {
                    var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                    vin = query["vin"];
                }

                // Validate VIN
                if (string.IsNullOrWhiteSpace(vin))
                {
                    _logger.LogWarning("⚠️ Empty VIN provided");
                    response.StatusCode = HttpStatusCode.BadRequest;
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "VIN is required",
                        message = "Please provide a valid VIN number"
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true
                    }));
                    
                    return response;
                }

                // Clean VIN (remove spaces, uppercase)
                vin = vin.Trim().ToUpper();

                _logger.LogInformation("🔍 DecodeVin function started - VIN: {vin}", vin);

                // Call NHTSA API
                var nhtsaApiUrl = $"https://vpic.nhtsa.dot.gov/api/vehicles/decodevinvalues/{vin}?format=json";
                
                using var httpClient = _httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);
                
                var nhtsaResponse = await httpClient.GetAsync(nhtsaApiUrl);
                
                if (!nhtsaResponse.IsSuccessStatusCode)
                {
                    _logger.LogError("❌ NHTSA API returned status code: {statusCode}", nhtsaResponse.StatusCode);
                    response.StatusCode = HttpStatusCode.BadGateway;
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "Failed to decode VIN",
                        message = "The VIN decoding service is currently unavailable"
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true
                    }));
                    
                    return response;
                }

                var nhtsaContent = await nhtsaResponse.Content.ReadAsStringAsync();
                var nhtsaData = JsonSerializer.Deserialize<NhtsaResponse>(nhtsaContent);

                if (nhtsaData?.Results == null || nhtsaData.Results.Count == 0)
                {
                    _logger.LogWarning("⚠️ No results returned from NHTSA for VIN: {vin}", vin);
                    response.StatusCode = HttpStatusCode.NotFound;
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "No data found",
                        message = "Could not decode the provided VIN"
                    }, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true
                    }));
                    
                    return response;
                }

                var result = nhtsaData.Results[0];
                
                // Check for errors or warnings
                var hasErrors = !string.IsNullOrWhiteSpace(result.AdditionalErrorText);
                var errorLevel = DetermineErrorLevel(result.AdditionalErrorText);

                // Filter out empty/null values and "Not Applicable" values
                var filteredData = FilterVehicleData(result);

                // Build response
                var responseData = new
                {
                    success = true,
                    vin = vin,
                    hasWarnings = hasErrors,
                    errorLevel = errorLevel.ToString().ToLower(),
                    warnings = hasErrors ? new[] { result.AdditionalErrorText } : Array.Empty<string>(),
                    data = filteredData,
                    rawMessage = nhtsaData.Message,
                    timestamp = DateTime.UtcNow
                };

                response.StatusCode = HttpStatusCode.OK;
                await response.WriteStringAsync(JsonSerializer.Serialize(responseData, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }));

                _logger.LogInformation("✅ Successfully decoded VIN: {vin}", vin);
                return response;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "❌ HTTP error while calling NHTSA API");
                response.StatusCode = HttpStatusCode.BadGateway;
                
                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "API communication error",
                    message = "Unable to reach VIN decoding service"
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }));
                
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error in DecodeVin function");
                response.StatusCode = HttpStatusCode.InternalServerError;
                
                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Internal error",
                    message = "An unexpected error occurred while processing your request"
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }));
                
                return response;
            }
        }

        private ErrorLevel DetermineErrorLevel(string? errorText)
        {
            if (string.IsNullOrWhiteSpace(errorText))
                return ErrorLevel.None;

            var lowerError = errorText.ToLower();
            
            if (lowerError.Contains("may be incorrect") || lowerError.Contains("unused position"))
                return ErrorLevel.Warning;
            
            if (lowerError.Contains("invalid") || lowerError.Contains("error"))
                return ErrorLevel.Error;
            
            return ErrorLevel.Info;
        }

        private Dictionary<string, object> FilterVehicleData(VehicleResult result)
        {
            var filtered = new Dictionary<string, object>();
            var properties = typeof(VehicleResult).GetProperties();

            foreach (var prop in properties)
            {
                var value = prop.GetValue(result);
                
                // Skip if value is null, empty string, or "Not Applicable"
                if (value == null)
                    continue;
                    
                if (value is string stringValue)
                {
                    if (string.IsNullOrWhiteSpace(stringValue) || 
                        stringValue.Equals("Not Applicable", StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                
                // Convert property name to camelCase
                var propertyName = char.ToLowerInvariant(prop.Name[0]) + prop.Name.Substring(1);
                filtered[propertyName] = value;
            }

            return filtered;
        }

        private enum ErrorLevel
        {
            None,
            Info,
            Warning,
            Error
        }

        // NHTSA API Response Models
        private class NhtsaResponse
        {
            [JsonPropertyName("Count")]
            public int Count { get; set; }

            [JsonPropertyName("Message")]
            public string? Message { get; set; }

            [JsonPropertyName("SearchCriteria")]
            public string? SearchCriteria { get; set; }

            [JsonPropertyName("Results")]
            public List<VehicleResult>? Results { get; set; }
        }

        private class VehicleResult
        {
            public string? ABS { get; set; }
            public string? ActiveSafetySysNote { get; set; }
            public string? AdaptiveCruiseControl { get; set; }
            public string? AdaptiveDrivingBeam { get; set; }
            public string? AdaptiveHeadlights { get; set; }
            public string? AdditionalErrorText { get; set; }
            public string? AirBagLocCurtain { get; set; }
            public string? AirBagLocFront { get; set; }
            public string? AirBagLocKnee { get; set; }
            public string? AirBagLocSeatCushion { get; set; }
            public string? AirBagLocSide { get; set; }
            public string? AutoReverseSystem { get; set; }
            public string? AutomaticPedestrianAlertingSound { get; set; }
            public string? AxleConfiguration { get; set; }
            public string? Axles { get; set; }
            public string? BasePrice { get; set; }
            public string? BatteryA { get; set; }
            public string? BatteryA_to { get; set; }
            public string? BatteryCells { get; set; }
            public string? BatteryInfo { get; set; }
            public string? BatteryKWh { get; set; }
            public string? BatteryKWh_to { get; set; }
            public string? BatteryModules { get; set; }
            public string? BatteryPacks { get; set; }
            public string? BatteryType { get; set; }
            public string? BatteryV { get; set; }
            public string? BatteryV_to { get; set; }
            public string? BedLengthIN { get; set; }
            public string? BedType { get; set; }
            public string? BlindSpotIntervention { get; set; }
            public string? BlindSpotMon { get; set; }
            public string? BodyCabType { get; set; }
            public string? BodyClass { get; set; }
            public string? BrakeSystemDesc { get; set; }
            public string? BrakeSystemType { get; set; }
            public string? BusFloorConfigType { get; set; }
            public string? BusLength { get; set; }
            public string? BusType { get; set; }
            public string? CAN_AACN { get; set; }
            public string? CIB { get; set; }
            public string? CashForClunkers { get; set; }
            public string? ChargerLevel { get; set; }
            public string? ChargerPowerKW { get; set; }
            public string? CombinedBrakingSystem { get; set; }
            public string? CoolingType { get; set; }
            public string? CurbWeightLB { get; set; }
            public string? CustomMotorcycleType { get; set; }
            public string? DaytimeRunningLight { get; set; }
            public string? DestinationMarket { get; set; }
            public string? DisplacementCC { get; set; }
            public string? DisplacementCI { get; set; }
            public string? DisplacementL { get; set; }
            public string? Doors { get; set; }
            public string? DriveType { get; set; }
            public string? DriverAssist { get; set; }
            public string? DynamicBrakeSupport { get; set; }
            public string? EDR { get; set; }
            public string? ESC { get; set; }
            public string? EVDriveUnit { get; set; }
            public string? ElectrificationLevel { get; set; }
            public string? EngineConfiguration { get; set; }
            public string? EngineCycles { get; set; }
            public string? EngineCylinders { get; set; }
            public string? EngineHP { get; set; }
            public string? EngineHP_to { get; set; }
            public string? EngineKW { get; set; }
            public string? EngineManufacturer { get; set; }
            public string? EngineModel { get; set; }
            public string? EntertainmentSystem { get; set; }
            public string? ErrorCode { get; set; }
            public string? ErrorText { get; set; }
            public string? ForwardCollisionWarning { get; set; }
            public string? FuelInjectionType { get; set; }
            public string? FuelTankMaterial { get; set; }
            public string? FuelTankType { get; set; }
            public string? FuelTypePrimary { get; set; }
            public string? FuelTypeSecondary { get; set; }
            public string? GCWR { get; set; }
            public string? GCWR_to { get; set; }
            public string? GVWR { get; set; }
            public string? GVWR_to { get; set; }
            public string? KeylessIgnition { get; set; }
            public string? LaneCenteringAssistance { get; set; }
            public string? LaneDepartureWarning { get; set; }
            public string? LaneKeepSystem { get; set; }
            public string? LowerBeamHeadlampLightSource { get; set; }
            public string? Make { get; set; }
            public string? MakeID { get; set; }
            public string? Manufacturer { get; set; }
            public string? ManufacturerId { get; set; }
            public string? Model { get; set; }
            public string? ModelID { get; set; }
            public string? ModelYear { get; set; }
            public string? MotorcycleChassisType { get; set; }
            public string? MotorcycleSuspensionType { get; set; }
            public string? NCSABodyType { get; set; }
            public string? NCSAMake { get; set; }
            public string? NCSAMapExcApprovedBy { get; set; }
            public string? NCSAMapExcApprovedOn { get; set; }
            public string? NCSAMappingException { get; set; }
            public string? NCSAModel { get; set; }
            public string? NCSANote { get; set; }
            public string? NonLandUse { get; set; }
            public string? Note { get; set; }
            public string? OtherBusInfo { get; set; }
            public string? OtherEngineInfo { get; set; }
            public string? OtherMotorcycleInfo { get; set; }
            public string? OtherRestraintSystemInfo { get; set; }
            public string? OtherTrailerInfo { get; set; }
            public string? ParkAssist { get; set; }
            public string? PedestrianAutomaticEmergencyBraking { get; set; }
            public string? PlantCity { get; set; }
            public string? PlantCompanyName { get; set; }
            public string? PlantCountry { get; set; }
            public string? PlantState { get; set; }
            public string? PossibleValues { get; set; }
            public string? Pretensioner { get; set; }
            public string? RearAutomaticEmergencyBraking { get; set; }
            public string? RearCrossTrafficAlert { get; set; }
            public string? RearVisibilitySystem { get; set; }
            public string? SAEAutomationLevel { get; set; }
            public string? SAEAutomationLevel_to { get; set; }
            public string? SeatBeltsAll { get; set; }
            public string? SeatRows { get; set; }
            public string? Seats { get; set; }
            public string? SemiautomaticHeadlampBeamSwitching { get; set; }
            public string? Series { get; set; }
            public string? Series2 { get; set; }
            public string? SteeringLocation { get; set; }
            public string? SuggestedVIN { get; set; }
            public string? TPMS { get; set; }
            public string? TopSpeedMPH { get; set; }
            public string? TrackWidth { get; set; }
            public string? TractionControl { get; set; }
            public string? TrailerBodyType { get; set; }
            public string? TrailerLength { get; set; }
            public string? TrailerType { get; set; }
            public string? TransmissionSpeeds { get; set; }
            public string? TransmissionStyle { get; set; }
            public string? Trim { get; set; }
            public string? Trim2 { get; set; }
            public string? Turbo { get; set; }
            public string? VIN { get; set; }
            public string? ValveTrainDesign { get; set; }
            public string? VehicleDescriptor { get; set; }
            public string? VehicleType { get; set; }
            public string? WheelBaseLong { get; set; }
            public string? WheelBaseShort { get; set; }
            public string? WheelBaseType { get; set; }
            public string? WheelSizeFront { get; set; }
            public string? WheelSizeRear { get; set; }
            public string? WheelieMitigation { get; set; }
            public string? Wheels { get; set; }
            public string? Windows { get; set; }
        }
    }
}
