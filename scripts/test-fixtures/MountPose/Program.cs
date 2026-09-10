using System.Reflection;
using ServerMap.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;

if (args.Length != 1) throw new ArgumentException("GameRoot required; original assets are read locally, never copied into the repository");
var game = Path.GetFullPath(args[0]);
AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
{
    var name = new AssemblyName(request.Name).Name + ".dll";
    foreach (var folder in new[] { game, Path.Combine(game,"Lib"), Path.Combine(game,"Mods") })
    { var path = Path.Combine(folder,name); if (File.Exists(path)) return Assembly.LoadFrom(path); }
    return null;
};
Checks.Run(game);

static class Checks
{
    private static void Require(bool value, string message) { if(!value) throw new Exception(message); }
    private static bool Same(float[] a,float[] b) => a.Length==b.Length && a.Zip(b).All(p=>Math.Abs(p.First-p.Second)<.00001f);
    private static IEnumerable<ShapeElement> Elements(IEnumerable<ShapeElement> roots) => roots.SelectMany(e=>new[]{e}.Concat(Elements(e.Children??[])));
    private static IEnumerable<ElementPose> Poses(IEnumerable<ElementPose> roots) => roots.SelectMany(e=>new[]{e}.Concat(Poses(e.ChildElementPoses??[])));
    public static void Run(string game)
    {
        // Fixture-only setting; accommodates all joints without needing a game graphics context.
        GlobalConstants.MaxAnimatedElements=512;
        var path=Path.Combine(game,"assets/survival/shapes/entity/animal/mammal/hooved/deer/elk/female.json");
        var shape=JsonUtil.FromString<Shape>(File.ReadAllText(path));
        var head=Elements(shape!.Elements).Single(e=>e.Name=="Head");
        var accessory=new ShapeElement {Name="MapTestAccessory",From=[0,0,0],To=[1,1,1],RotationOrigin=[0,0,0]};
        head.Children=[..head.Children??[],accessory];
        var logger=DispatchProxy.Create<ILogger,SilentLogger>();
        shape!.InitForAnimations(logger,"elk-pose-fixture");
        var max=shape.JointsById.Keys.Max();
        var idle=shape.Animations.Single(a=>a.Code.Equals("idle",StringComparison.OrdinalIgnoreCase));
        var scratch=typeof(Animation).GetField("jointsDone",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var idleScratch=(HashSet<int>)scratch.GetValue(idle)!;idleScratch.Add(98765);
        var references=idle.KeyFrames.SelectMany(k=>k.Elements.Values).Select(k=>(Key:k,Element:k.ForElement)).ToArray();
        var expected=MountIdlePose.Matrices(true,new ClientAnimator(()=>1,shape.Animations,shape.Elements,shape.JointsById),max,"Idle");
        Require(idle.PrevNextKeyFrameByFrame==null&&idleScratch.SetEquals([98765]),"Idle's shared frame caches and mutable scratch set must stay untouched");
        var reference=new ClientAnimator(()=>1,shape.Animations,shape.Elements,shape.JointsById);
        var referenceMeta=new AnimationMetaData{Code="idle",Animation="idle",Weight=1,BlendMode=EnumAnimationBlendMode.Average}.Init();
        var referenceActive=new Dictionary<string,AnimationMetaData>{{"idle",referenceMeta}};
        reference.OnFrame(referenceActive,0);reference.GetAnimationState("idle").EasingFactor=1;reference.OnFrame(referenceActive,0);
        Require(Same(expected,reference.Matrices),"Independent pose must match the game's actual idle pose");
        foreach(var name in new[]{"L ArmUp","L ArmMid","L ArmLow","R ArmUp","R ArmMid","R ArmLow"})
        {
            var pose=reference.GetPosebyName(name);
            Require(Math.Abs(pose.degX)+Math.Abs(pose.degY)+Math.Abs(pose.degZ)<.0001,"Idle must leave the authored front limb unbent: "+name);
        }
        Require(accessory.JointId==head.JointId,"Synthetic attached gear must follow the parent's joint");
        Require(expected.Skip(head.JointId*16).Take(16).Where((v,i)=>Math.Abs(v-Vintagestory.API.MathTools.Mat4f.Create()[i])>.001).Any(),"Idle head pose is actually evaluated, not replaced with all-identity bind matrices");

        var checkedFrames=0;var bentSeen=false;
        foreach(var code in new[]{"walk","trot","run","jump","swim"})
        {
            var movement=shape.Animations.FirstOrDefault(a=>a.Code.Equals(code,StringComparison.OrdinalIgnoreCase));
            if(movement==null)continue;
            foreach(var phase in new[]{0f,.25f,.5f,.75f})
            {
                var live=new ClientAnimator(()=>2,shape.Animations,shape.Elements,shape.JointsById);
                var meta=new AnimationMetaData{Code=movement.Code,Animation=movement.Code,Weight=1,BlendMode=EnumAnimationBlendMode.Average}.Init();
                var active=new Dictionary<string,AnimationMetaData>{{movement.Code,meta}};
                live.OnFrame(active,0);var running=live.GetAnimationState(movement.Code);running.EasingFactor=1;running.CurrentFrame=(movement.QuantityFrames-1)*phase;live.OnFrame(active,0);
                var before=live.Matrices.ToArray();var frame=running.CurrentFrame;var easing=running.EasingFactor;
                var poses=Poses(live.RootPoses).Select(p=>(Pose:p,Matrix:p.AnimModelMatrix.ToArray())).ToArray();
                var inverse=Elements(shape.Elements).Select(e=>(Element:e,Reference:e.inverseModelTransform,Values:e.inverseModelTransform!.ToArray())).ToArray();
                var snapshot=MountIdlePose.Matrices(true,live,max,"idle");
                Require(Same(expected,snapshot),"Captured standing pose must not depend on "+code+" frame "+phase);
                bentSeen|=!Same(before,snapshot);
                Require(Same(before,live.Matrices)&&running.CurrentFrame==frame&&running.EasingFactor==easing&&active.Count==1,"Capture changed the live animation");
                Require(poses.All(p=>Same(p.Matrix,p.Pose.AnimModelMatrix)),"Capture changed live element poses");
                Require(inverse.All(p=>ReferenceEquals(p.Reference,p.Element.inverseModelTransform)&&Same(p.Values,p.Element.inverseModelTransform!)),"Capture rewrote live inverse-bind caches");
                var vehicle=MountIdlePose.Matrices(false,live,max);
                Require(Same(before,vehicle)&&!ReferenceEquals(vehicle,live.Matrices),"Vehicle state must remain a copied snapshot, not animal idle");
                checkedFrames++;
            }
        }
        Require(checkedFrames>=12&&bentSeen,"Exercise real locomotion poses differing from standing");
        Require(references.All(r=>ReferenceEquals(r.Element,r.Key.ForElement)),"Capture rebound original animation keyframes");
        var missingIdle=new ClientAnimator(()=>1,shape.Animations.Where(a=>a!=idle).ToArray(),shape.Elements,shape.JointsById);
        // Explicitly absent standing codes must use neutral bind pose, not an arbitrary locomotion frame.
        var neutral=MountIdlePose.Matrices(true,missingIdle,max,"nonexistent-standing");
        for(var i=0;i<=max;i++)Require(Same(neutral.Skip(i*16).Take(16).ToArray(),Vintagestory.API.MathTools.Mat4f.Create()),"Missing idle fallback must be neutral");
        Console.WriteLine($"PASS actual elk model: {checkedFrames} movement phases -> stable straight front limbs; idle head/attached gear retained; live poses, frame time, caches and keyframes unchanged; vehicle snapshot and no-idle fallback");
    }
}
public class SilentLogger : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method,object?[]? args) => method?.ReturnType.IsValueType==true&&method.ReturnType!=typeof(void)?Activator.CreateInstance(method.ReturnType):null;
}
