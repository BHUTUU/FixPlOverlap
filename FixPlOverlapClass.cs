using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Geometry;

namespace FixPlOverlap
{
    public class FixPlOverlapClass
    {
        [CommandMethod("FixPlOverlap")]
        public void FixPlOverlap()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;
            ObjectId resultantRegionId = ObjectId.Null;

            PromptEntityOptions plEntity = new PromptEntityOptions("\nSelect the first polyline: ");
            plEntity.SetRejectMessage("\nSelected entity is not a polyline.");
            plEntity.AddAllowedClass(typeof(Polyline), true);
            PromptEntityResult plObj = ed.GetEntity(plEntity);

            if (plObj.Status != PromptStatus.OK) return;
            
            ed.SetImpliedSelection(new ObjectId[] { plObj.ObjectId });
            ed.Regen();

            PromptEntityOptions plEntity2 = new PromptEntityOptions("\nSelect the second polyline: ");
            plEntity2.SetRejectMessage("\nSelected entity is not a polyline.");
            plEntity2.AddAllowedClass(typeof(Polyline), true);
            PromptEntityResult pl2Obj = ed.GetEntity(plEntity2);

            ed.SetImpliedSelection(new ObjectId[0]);
            ed.Regen();

            if (pl2Obj.Status != PromptStatus.OK) return;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Polyline cpoly1 = tr.GetObject(plObj.ObjectId, OpenMode.ForRead) as Polyline;
                Polyline cpoly2 = tr.GetObject(pl2Obj.ObjectId, OpenMode.ForRead) as Polyline;

                if(cpoly1 == null || cpoly2 == null)
                {
                    ed.WriteMessage("\nOne or both selected entities are not valid closed polylines.");
                    return;
                }

                if (!cpoly1.Closed)
                {
                    if (!cpoly2.Closed)
                    {
                        ed.WriteMessage("\nBoth selected polylines are not closed.");
                        return;
                    }
                    ed.WriteMessage("\nThe first selected polyline is not closed.");
                    return;
                }
                if (!cpoly2.Closed)
                {
                    ed.WriteMessage("\nThe second selected polyline is not closed.");
                    return;
                }

                using Polyline pl1 = (Polyline)cpoly1.Clone();
                using Polyline pl2 = (Polyline)cpoly2.Clone();
                DBObjectCollection curves1 = new DBObjectCollection();
                curves1.Add(pl1);
                
                DBObjectCollection curves2 = new DBObjectCollection();
                curves2.Add(pl2);

                DBObjectCollection cpl1Region = Region.CreateFromCurves(curves1);
                DBObjectCollection cpl2Region = Region.CreateFromCurves(curves2);

                if (cpl1Region.Count == 0 || cpl2Region.Count == 0)
                {
                    ed.WriteMessage("\nFailed while creating polylines to regions so that they could be subtracted.");
                    return;
                }

                Region reg1 = cpl1Region[0] as Region;
                Region reg2 = cpl2Region[0] as Region;

                cpoly1.UpgradeOpen();
                cpoly1.Erase();


                try
                {
                    reg1.BooleanOperation(BooleanOperationType.BoolSubtract, reg2);
                    if(reg1.Area <= 0)
                    {
                        ed.WriteMessage("\nNo Intersection found.");
                        reg1.Dispose();
                        reg2.Dispose();
                        return;
                    }
                }
                catch(System.Exception ex)
                {
                    ed.WriteMessage($"{ex.Message}");
                    return;
                }
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                btr.AppendEntity(reg1);
                tr.AddNewlyCreatedDBObject(reg1, true);
                reg2.Dispose();
                DBObjectCollection dbObjectCollection = PolylineFromRegion(reg1);

                foreach(Entity polyUnit in dbObjectCollection)
                {
                    btr.AppendEntity(polyUnit);
                    tr.AddNewlyCreatedDBObject(polyUnit, true);
                }
                reg1.Erase();

                tr.Commit();
            }
        }
        public static DBObjectCollection PolylineFromRegion(Region region)
        {
            DBObjectCollection resultant = new DBObjectCollection();
            DBObjectCollection curves = new DBObjectCollection();
            region.Explode(curves);
            Plane pl = new Plane(new Point3d(0, 0, 0), region.Normal);
            using (pl)
            {
                bool completed = false;
                while (!completed && curves.Count > 0)
                {
                    int curveCount = 0, nonCurveCount = 0, firstCurveIndex = -1;
                    for (int i = 0; i < curves.Count; i++)
                    {
                        Curve tempCurve = curves[i] as Curve;
                        if (tempCurve == null) nonCurveCount++;
                        else
                        {
                            if (tempCurve.Closed)
                            {
                                resultant.Add(tempCurve);
                                curves.Remove(tempCurve);
                                i--;
                            }
                            else
                            {
                                curveCount++;
                                if (firstCurveIndex == -1)
                                    firstCurveIndex = i;
                            }
                        }
                    }
                    if (firstCurveIndex >= 0)
                    {
                        Curve firstCurve = (Curve)curves[firstCurveIndex];
                        Polyline constructedPline = new Polyline();
                        constructedPline.SetPropertiesFrom(region);
                        constructedPline.AddVertexAt(constructedPline.NumberOfVertices, firstCurve.StartPoint.Convert2d(pl), BulgeFromCurve(firstCurve, false), 0, 0);
                        constructedPline.AddVertexAt(constructedPline.NumberOfVertices, firstCurve.EndPoint.Convert2d(pl), 0, 0, 0);
                        curves.Remove(firstCurve);
                        Point3d nextPoint = firstCurve.EndPoint;
                        firstCurve.Dispose();
                        int previousCount = curves.Count + 1;
                        while (curves.Count > nonCurveCount && curves.Count < previousCount)
                        {
                            previousCount = curves.Count;
                            foreach (DBObject obj in curves)
                            {
                                Curve currentCurve = obj as Curve;
                                if (currentCurve != null)
                                {
                                    if (currentCurve.StartPoint == nextPoint || currentCurve.EndPoint == nextPoint)
                                    {
                                        double bulge = BulgeFromCurve(currentCurve, currentCurve.EndPoint == nextPoint);
                                        if (bulge != 0.0) constructedPline.SetBulgeAt(constructedPline.NumberOfVertices - 1, bulge);
                                        if (currentCurve.StartPoint == nextPoint) nextPoint = currentCurve.EndPoint; else nextPoint = currentCurve.StartPoint;
                                        constructedPline.AddVertexAt(constructedPline.NumberOfVertices, nextPoint.Convert2d(pl), 0, 0, 0);
                                        curves.Remove(currentCurve);
                                        currentCurve.Dispose();
                                        break;
                                    }
                                }
                            }
                        }
                        constructedPline.TransformBy(Matrix3d.PlaneToWorld(pl));
                        resultant.Add(constructedPline);
                        if (curves.Count == nonCurveCount) completed = true;
                    }
                    if (nonCurveCount > 0 && curves.Count > 0)
                    {
                        foreach (DBObject obj in curves)
                        {
                            Region subRegion = obj as Region;
                            if (subRegion != null)
                            {
                                DBObjectCollection sunReasonToPolyline = PolylineFromRegion(subRegion);
                                foreach (DBObject o in sunReasonToPolyline) resultant.Add(o);
                                curves.Remove(subRegion);
                                subRegion.Dispose();
                            }
                        }
                    }
                    if (curves.Count == 0) completed = true;
                }
            }
            return resultant;
        }
        public static double BulgeFromCurve(Curve cv, bool clockwise)
        {
            double bulge = 0.0;
            Arc a = cv as Arc;
            if (a != null)
            {
                double newStart;
                if (a.StartAngle > a.EndAngle) newStart = a.StartAngle - 2 * Math.PI; else newStart = a.StartAngle; bulge = Math.Tan((a.EndAngle - newStart) / 4);
                if (clockwise) bulge = -bulge;
            }
            return bulge;
        }
    }
}
