using Elements;
using Elements.GeoJSON;
using Elements.Geometry;
using Elements.Geometry.Solids;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using SharpMap;
using SharpMap.Data.Providers;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Xml;

namespace WFSTest
{
    public static class WFSTest
    {
        /// <summary>
        /// The WFSTest function.
        /// </summary>
        /// <param name="model">The input model.</param>
        /// <param name="input">The arguments to the execution.</param>
        /// <returns>A WFSTestOutputs instance containing computed results and the model with any new elements.</returns>
        public static WFSTestOutputs Execute(Dictionary<string, Model> inputModels, WFSTestInputs input)
        {
            var output = new WFSTestOutputs();
            var origin = inputModels["location"].AllElementsOfType<Origin>().First();

            // tools for mapping between coordinate systems
            CoordinateTransformationFactory ctfac = new CoordinateTransformationFactory();
            var csFac = new CoordinateSystemFactory();
            // I found this WKT by googling EPSG:25832
            var epsg25832 = "PROJCS[\"ETRS89_UTM_zone_32N\",GEOGCS[\"GCS_ETRS_1989\",DATUM[\"D_ETRS_1989\",SPHEROID[\"GRS_1980\",6378137,298.257222101]],PRIMEM[\"Greenwich\",0],UNIT[\"Degree\",0.017453292519943295]],PROJECTION[\"Transverse_Mercator\"],PARAMETER[\"latitude_of_origin\",0],PARAMETER[\"central_meridian\",9],PARAMETER[\"scale_factor\",0.9996],PARAMETER[\"false_easting\",500000],PARAMETER[\"false_northing\",0],UNIT[\"Meter\",1]]";
            // convert the wkt to a coordinate system
            var fromCS = csFac.CreateFromWkt(epsg25832);
            var toCS = ProjNet.CoordinateSystems.GeographicCoordinateSystem.WGS84;
            ICoordinateTransformation FromUTM32ToLatLon = ctfac.CreateFromCoordinateSystems(fromCS, toCS);
            ICoordinateTransformation FromLatLonToUTM32 = ctfac.CreateFromCoordinateSystems(toCS, fromCS);

            var bounds = input.Bounds;
            var bbox = new BBox3(bounds.Vertices);
            var latLonMin = Position.FromVectorMeters(origin.Position, bbox.Min);
            var latLonMax = Position.FromVectorMeters(origin.Position, bbox.Max);

            var minInUTM = FromLatLonToUTM32.MathTransform.Transform(new double[] { latLonMin.Longitude, latLonMin.Latitude });
            var maxInUTM = FromLatLonToUTM32.MathTransform.Transform(new double[] { latLonMax.Longitude, latLonMax.Latitude });

            // get coordinates in EPSG::25832
            var minX = minInUTM[0];
            var minY = minInUTM[1];
            var maxX = maxInUTM[0];
            var maxY = maxInUTM[1];
            // fetch all data
            var gebaeudeTask = FetchDataSet(output, origin, FromUTM32ToLatLon, minX, minY, maxX, maxY, "AX_Gebaeude", false);
            var flurstueckTask = FetchDataSet(output, origin, FromUTM32ToLatLon, minX, minY, maxX, maxY, "AX_Flurstueck", true);
            Task.WaitAll(gebaeudeTask, flurstueckTask);
            // apply a different material to the gebäude layer
            gebaeudeTask.Result.ForEach((e) =>
            {
                e.Material = BuiltInMaterials.XAxis;
            });
            // add the elements to the model
            output.Model.AddElements(gebaeudeTask.Result);
            output.Model.AddElements(flurstueckTask.Result);

            return output;

        }

        private static async Task<List<GeometricElement>> FetchDataSet(WFSTestOutputs output, Origin origin, ICoordinateTransformation FromUTM32ToLatLon, double minX, double minY, double maxX, double maxY, string dataSetToFetch, bool createAsCurve)
        {
            var elements = new List<GeometricElement>();
            var url = $"https://www.wfs.nrw.de/geobasis/wfs_nw_alkis_aaa-modell-basiert?VERSION=2.0.0&SERVICE=WFS&REQUEST=GetFeature&TYPENAMES={dataSetToFetch}&BBOX={minX},{minY},{maxX},{maxY},urn:ogc:def:crs:EPSG::25832";

            var request = WebRequest.Create(url);
            request.Method = "GET";
            var webResponse = await request.GetResponseAsync();
            var webStream = webResponse.GetResponseStream();
            using (var reader = new StreamReader(webStream))
            {

                var data = reader.ReadToEnd();
                var xml = new XmlDocument();
                xml.LoadXml(data);

                //you could parse the xml directly, but I find json easier to work with.
                var json = JsonConvert.SerializeXmlNode(xml);
                var root = JsonConvert.DeserializeObject<JObject>(json);
                var featureCollection = root["wfs:FeatureCollection"];
                var members = featureCollection["wfs:member"];
                foreach (var member in members)
                {
                    var typeObject = member[dataSetToFetch] as JObject;
                    var position = typeObject["position"];
                    try
                    {
                        Elements.Geometry.Polygon polygon = GetPolygonFromPosition(origin, FromUTM32ToLatLon, position);
                        GeometricElement element;
                        if (createAsCurve)
                        {
                            element = new ModelCurve(polygon);

                        }
                        else
                        {
                            element = new GeometricElement();
                            var representation = new Lamina(polygon);
                            element.Representation = representation;

                        }
                        foreach (var property in typeObject)
                        {
                            if (property.Key == "position")
                            {
                                continue;
                            }
                            element.AdditionalProperties[property.Key] = property.Value;
                        }
                        element.Name = dataSetToFetch;
                        elements.Add(element);
                    }
                    catch
                    {
                        // TODO: handle errors in processing
                    }

                }
            }
            return elements;
        }

        private static Elements.Geometry.Polygon GetPolygonFromPosition(Origin origin, ICoordinateTransformation FromUTM32ToLatLon, JToken position)
        {
            // again, there are probably libraries for parsing gml,
            // but digging in the JSON representation was easy enough. This will
            // break down if there's much variance in the construction of the 
            // geometry coming from different WFS services.
            var pgon = position["gml:Polygon"];
            var exterior = pgon["gml:exterior"];
            var linearRing = exterior["gml:LinearRing"];
            var posList = linearRing["gml:posList"];
            var coords = posList.ToString().Split(' ');
            var vectors = new List<Vector3>();
            for (int i = 0; i < coords.Length; i += 3)
            {
                var x = Double.Parse(coords[i]);
                var y = Double.Parse(coords[i + 1]);
                var z = Double.Parse(coords[i + 2]);
                var lonlat = FromUTM32ToLatLon.MathTransform.Transform(new double[] { x, y });
                var pos = new Position(lonlat[1], lonlat[0]);
                var originElevation = origin.Elevation;
                vectors.Add(pos.ToVectorMeters(origin.Position));
            }

            var polygon = new Elements.Geometry.Polygon(vectors);
            return polygon;
        }
    }
}