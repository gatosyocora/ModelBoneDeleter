using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.ObjectModel;
using UnityEditor.IMGUI.Controls;
using System.Text.RegularExpressions;
using System.IO;

/*
 * Copyright (c) 2019 gatosyocora
 * Released under the MIT license.
 * see LICENSE.txt
 */

// ModelBoneDeleter v1.1.3

namespace Gatosyocora.ModelBoneDeleter
{
    public class ModelBoneDeleter : EditorWindow
    {
        private GameObject avatar;
        private List<BoneInfo> rootBoneList;
        private bool duplicateAvatar = true;
        private int boneCount = 0;
        
        private TreeViewState treeViewState;
        private BoneTreeView treeView;
        private string saveFolder = "Assets/";

        [MenuItem("GatoTool/ModelBoneDeleter")]
        private static void Open()
        {
            GetWindow<ModelBoneDeleter>("ModelBoneDeleter");
        }

        private void OnEnable()
        {
            SceneView.onSceneGUIDelegate += this.OnSceneGUI;

            if (treeViewState == null)
                treeViewState = new TreeViewState();

            avatar = null;
            boneCount = 0;
            rootBoneList = null;

        }

        private void OnDisable()
        {
            SceneView.onSceneGUIDelegate -= this.OnSceneGUI;
        }

        private void OnGUI()
        {
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                avatar = EditorGUILayout.ObjectField(
                            "avatar",
                            avatar,
                            typeof(GameObject),
                            true
                        ) as GameObject;


                if (check.changed && avatar != null)
                {
                    rootBoneList = GetRootBoneInfos(avatar);
                    boneCount = GetAvatarBoneCount(avatar, true);
                    treeView = CreateBoneTreeView(rootBoneList, treeViewState);
                    saveFolder = GetAvatarFbxPath(avatar);
                }
            }

            if (avatar != null && rootBoneList != null)
            {
                using (new EditorGUILayout.VerticalScope(GUI.skin.box))
                {
                    if (treeView != null)
                        treeView.OnGUI(EditorGUILayout.GetControlRect(false, position.height-120));
                }
            }

            EditorGUILayout.LabelField("BoneCount", boneCount.ToString());

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Mesh SaveFolder", saveFolder);

                if (GUILayout.Button("Select Folder", GUILayout.Width(100)))
                {
                    saveFolder = EditorUtility.OpenFolderPanel("Select saved folder", saveFolder, "");
                    var match = Regex.Match(saveFolder, @"Assets/.*");
                    saveFolder = match.Value + "/";
                    if (saveFolder == "/") saveFolder = "Assets/";
                }
            }

            duplicateAvatar = EditorGUILayout.ToggleLeft("Copy Avatar", duplicateAvatar);

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(avatar == null))
            {
                if (GUILayout.Button("Delete Bones"))
                {
                    var boneList = ConvertBoneTreeToBoneList(rootBoneList);

                    if (duplicateAvatar)
                    {
                        var name = avatar.name + "_deleteBones";
                        avatar = Instantiate(avatar);
                        avatar.name = name;
                        var copyBoneList = ConvertBoneTreeToBoneList(GetRootBoneInfos(avatar));

                        // copyBoneListにboneListのdeletedの選択状態をコピーする
                        for (int index = 0; index < boneList.Count(); index++)
                        {
                            if (boneList[index].deleted)
                                copyBoneList[index].deleted = true;
                        }
                        boneList = copyBoneList;
                    }

                    var deleteBoneList = boneList.Where(x => x.deleted).ToList();
                    DeleteAvatarBones(ref avatar, ref deleteBoneList);

                    // 削除後のボーンをGUIに反映
                    rootBoneList = GetRootBoneInfos(avatar);
                    boneCount = GetAvatarBoneCount(avatar, true);
                    treeView = CreateBoneTreeView(rootBoneList, treeViewState);
                }
            }

        }
        
        /// <summary>
        /// SceneViewにボーン位置を表示する
        /// </summary>
        /// <param name="sceneView"></param>
        private void OnSceneGUI(SceneView sceneView)
        {
            if (avatar != null && rootBoneList != null)
            {
                foreach (var rootBone in rootBoneList)
                {
                    ShowBoneToSceneView(rootBone);
                }
            }

            SceneView.lastActiveSceneView.Repaint();
        }

        /// <summary>
        /// ボーンを削除する
        /// </summary>
        /// <param name="avatar"></param>
        /// <param name="deleteBoneList">削除するボーンのリスト</param>
        private void DeleteAvatarBones(ref GameObject avatar, ref List<BoneInfo> deleteBoneList)
        {
            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>();

            // 一番下にある子ボーンから順にウェイト転送するために並べ替える
            var deleteBoneTransListDepthSorted = deleteBoneList.OrderByDescending(x => x.depth).Select(x=>x.boneTrans).ToList();

            var deleteBoneInstanceIDs = deleteBoneList.Select(x => x.boneTrans.gameObject.GetInstanceID()).ToArray<int>();

            // 重複なしのボーンのリストを取得
            var readOnlyBoneTrans =
                renderers
                .Select(x => x.bones)
                .Where(x => x != null)
                .SelectMany(x => x)
                .Distinct()
                .ToList()
                .AsReadOnly();

            Mesh mesh, mesh_custom;
            foreach (var renderer in renderers)
            {
                bool changed = false;
                mesh = renderer.sharedMesh;
                mesh_custom = Instantiate(mesh);

                var boneTrans = renderer.bones;
                var boneWeights = mesh_custom.boneWeights;

                if (renderer.rootBone == null) continue;
                
                var deleteBoneIndexList = new List<int>();

                foreach (var deleteBoneTrans in deleteBoneTransListDepthSorted)
                {

                    int deleteBoneIndex = ArrayUtility.IndexOf<Transform>(boneTrans, deleteBoneTrans);
                    if (deleteBoneIndex == -1) continue;

                    deleteBoneIndexList.Add(deleteBoneIndex);

                    // ウェイト転送先を決定（直近の削除されない親ボーン）
                    var weightBone = boneTrans[deleteBoneIndex].parent;
                    while (true)
                    {
                        // 転送先ボーンがnullかrootBoneか削除ボーンに含まれていない &
                        // ボーンであれば削除（ボーンを束ねるための空オブジェクトが含まれる場合がある）
                        if ((weightBone == null ||
                            weightBone == renderer.rootBone ||
                            !deleteBoneTransListDepthSorted.Contains(weightBone)) &&
                            readOnlyBoneTrans.Contains(weightBone)
                            )
                            break;

                        weightBone = weightBone.parent;
                    }

                    int weightBoneIndex = (weightBone != null) ? ArrayUtility.IndexOf(boneTrans, weightBone) :
                                                                ArrayUtility.IndexOf(boneTrans, renderer.rootBone);

                    // ウェイト転送をする
                    for (int vertexIndex = 0; vertexIndex < boneWeights.Length; vertexIndex++)
                    {
                        if (boneWeights[vertexIndex].boneIndex0 == deleteBoneIndex)
                        {
                            boneWeights[vertexIndex].boneIndex0 = weightBoneIndex;
                        }
                        if (boneWeights[vertexIndex].boneIndex1 == deleteBoneIndex)
                        {
                            boneWeights[vertexIndex].boneIndex1 = weightBoneIndex;
                        }
                        if (boneWeights[vertexIndex].boneIndex2 == deleteBoneIndex)
                        {
                            boneWeights[vertexIndex].boneIndex2 = weightBoneIndex;
                        }
                        if (boneWeights[vertexIndex].boneIndex3 == deleteBoneIndex)
                        {
                            boneWeights[vertexIndex].boneIndex3 = weightBoneIndex;
                        }
                    }
                    changed = true;
                }

                // 最終的にrenderer.bonesにnullが残るので削除するボーンは配列から除外する
                // ボーンのインデックスがずれるので各頂点に対応付けが必要
                if (changed)
                {
                    var boneList = boneTrans.ToList();
                    var bindposeList = mesh_custom.bindposes.ToList();

                    // インデックスが大きいものから順に処理していく
                    foreach (var deleteBoneIndex in deleteBoneIndexList.OrderByDescending(x=>x).ToArray())
                    {
                        for (int vertexIndex = 0; vertexIndex < boneWeights.Length; vertexIndex++)
                        {
                            if (boneWeights[vertexIndex].boneIndex0 > deleteBoneIndex)
                            {
                                boneWeights[vertexIndex].boneIndex0--;
                            }
                            if (boneWeights[vertexIndex].boneIndex1 > deleteBoneIndex)
                            {
                                boneWeights[vertexIndex].boneIndex1--;
                            }
                            if (boneWeights[vertexIndex].boneIndex2 > deleteBoneIndex)
                            {
                                boneWeights[vertexIndex].boneIndex2--;
                            }
                            if (boneWeights[vertexIndex].boneIndex3 > deleteBoneIndex)
                            {
                                boneWeights[vertexIndex].boneIndex3--;
                            }
                        }
                        boneList.Remove(boneTrans[deleteBoneIndex]);
                        bindposeList.Remove(mesh_custom.bindposes[deleteBoneIndex]);
                    }
                    renderer.bones = boneList.ToArray<Transform>();
                    mesh_custom.bindposes = bindposeList.ToArray<Matrix4x4>();
                }

                mesh_custom.boneWeights = boneWeights;

                // ウェイト情報が変更されたメッシュのみ書き出して適用
                if (changed)
                {
                    AssetDatabase.CreateAsset(mesh_custom, AssetDatabase.GenerateUniqueAssetPath(saveFolder + mesh.name + "_custom.asset"));
                    AssetDatabase.SaveAssets();

                    renderer.sharedMesh = mesh_custom;
                }
            }

            // ボーンをUndoできるようにしたうえで削除
            foreach (var trans in avatar.GetComponentsInChildren<Transform>())
            {
                if (trans != null && deleteBoneTransListDepthSorted.Contains(trans))
                {
                    Undo.DestroyObjectImmediate(trans.gameObject);
                }
            }
        }

        /// <summary>
        /// SceneViewにboneInfoのボーンを表示する
        /// </summary>
        /// <param name="boneInfo"></param>
        private void ShowBoneToSceneView(BoneInfo boneInfo)
        {
            if (boneInfo == null || boneInfo.boneTrans == null
                || boneInfo.childs == null) return;

            foreach (var childBoneInfo in boneInfo.childs)
            {
                if (childBoneInfo == null) continue;
                ShowBoneToSceneView(childBoneInfo, boneInfo);
            }
        }
        
        private void ShowBoneToSceneView(BoneInfo boneInfo, BoneInfo parentBoneInfo)
        {
            if (boneInfo == null || boneInfo.boneTrans == null) return;
            
            Vector3 pos1, pos2;

            if (boneInfo.deleted)
                Handles.color = Color.green;
            else
                Handles.color = Color.red;

            pos1 = parentBoneInfo.boneTrans.position;
            pos2 = boneInfo.boneTrans.position;
            Handles.DrawLine(pos1, pos2);

            if (boneInfo.childs == null) return;

            foreach (var childBoneInfo in boneInfo.childs)
            {
                if (childBoneInfo == null) continue;
                ShowBoneToSceneView(childBoneInfo, boneInfo);
            }
        }

        /// <summary>
        /// ボーンの数を取得する
        /// </summary>
        /// <param name="avatar"></param>
        /// <param name="containDisableObject"></param>
        /// <returns></returns>
        private int GetAvatarBoneCount(GameObject avatar, bool containDisableObject)
        {
            return
                avatar.GetComponentsInChildren<SkinnedMeshRenderer>(containDisableObject)
                .Select(x => x.bones)
                .Where(x => x != null)
                .SelectMany(x => x)
                .Distinct()
                .ToList()
                .Count();
        }

        /// <summary>
        /// ボーン情報を取得する
        /// </summary>
        /// <param name="avatar"></param>
        /// <returns></returns>
        private static List<BoneInfo> GetRootBoneInfos(GameObject avatar)
        {
            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            // 重複なしのボーンのリストを取得
            var readOnlyBoneTrans =
                renderers
                .Select(x => x.bones)
                .Where(x => x != null)
                .SelectMany(x => x)
                .Distinct()
                .ToList()
                .AsReadOnly();

            var boneInfos = new List<BoneInfo>();
            var containBones = new HashSet<Transform>();

            foreach (var rootBone in renderers.Select(x => x.rootBone).Where(x => x != null).ToArray())
            {
                if (!containBones.Contains(rootBone))
                {
                    boneInfos.Add(BoneToTreeNode(rootBone, ref readOnlyBoneTrans, ref containBones, 0));
                }

                containBones.Add(rootBone);
            }

            return boneInfos;
        }

        public class BoneInfo
        {
            public Transform boneTrans;
            public bool deleted;
            public List<BoneInfo> childs;
            public int depth;

            public BoneInfo(Transform bone, List<BoneInfo> childList, int depth)
            {
                this.boneTrans = bone;
                this.deleted = false;
                childs = childList;
                this.depth = depth;
            }

            public void ChangeChildDeletedState(bool deleted)
            {
                if (childs == null) return;

                foreach (var child in childs)
                {
                    child.deleted = deleted;

                    if (child.childs != null && child.childs.Count() > 0)
                        child.ChangeChildDeletedState(deleted);
                }
            }
        }

        /// <summary>
        /// avatarのfbxまたはPrefabのパスを取得する
        /// </summary>
        /// <param name="avatar"></param>
        /// <returns></returns>
        private string GetAvatarFbxPath(GameObject avatar)
        {
            var fbx = PrefabUtility.GetPrefabParent(avatar);
            if (fbx == null)
                return "Assets/";
            else
                return Path.GetDirectoryName(AssetDatabase.GetAssetPath(fbx)) + "/";
        }

        #region TreeView

        private BoneTreeView CreateBoneTreeView(List<BoneInfo> rootBoneInfos, TreeViewState treeViewState)
        {
            var treeView = new BoneTreeView(treeViewState);
            var currentId = 0;
            var root = new BoneTreeElement { Id = ++currentId, Name = "Bones" };
            for (int i = 0; i < rootBoneInfos.Count(); i++)
            {
                root.AddChild(BoneInfoToBoneTreeView(rootBoneInfos[i], ref currentId));
            }
            treeView.Setup(new List<BoneTreeElement> { root }.ToArray());

            return treeView;
        }

        private BoneTreeElement BoneInfoToBoneTreeView(BoneInfo bone, ref int id)
        {
            var element = new BoneTreeElement { Id = ++id, Name = bone.boneTrans.name, Bone = bone };

            if (bone.childs != null && bone.childs.Count() > 0)
            {
                foreach (var child in bone.childs)
                {
                    var childElement = BoneInfoToBoneTreeView(child, ref id);
                    element.AddChild(childElement);
                }
            }

            return element;
        }

        /// <summary>
        /// ボーンのTransformから木構造のノードに変換
        /// </summary>
        /// <param name="bone"></param>
        /// <param name="boneList"></param>
        /// <param name="containBones"></param>
        /// <returns></returns>
        private static BoneInfo BoneToTreeNode(Transform bone, ref ReadOnlyCollection<Transform> boneList, ref HashSet<Transform> containBones, int depth)
        {
            List<BoneInfo> childs = null;
            depth++;
            for (int i = 0; i < bone.childCount; i++)
            {
                var child = bone.GetChild(i);

                // childがボーンなら追加する
                if (boneList.Contains(child))
                {
                    if (!containBones.Contains(child))
                    {
                        if (childs == null) childs = new List<BoneInfo>();
                        childs.Add(BoneToTreeNode(child, ref boneList, ref containBones, depth));
                    }
                }
                else 
                {
                    // ボーンをまとめた空オブジェクトなのかどうか
                    if (ContainBoneInChild(child, ref boneList))
                    {
                        if (childs == null) childs = new List<BoneInfo>();
                        childs.Add(BoneToTreeNode(child, ref boneList, ref containBones, depth));
                    }
                }
            }
            return new BoneInfo(bone, childs, depth);
        }

        /// <summary>
        /// 特定のオブジェクト以下にボーンが含まれているか調べる
        /// </summary>
        /// <param name="objTrans"></param>
        /// <param name="boneList"></param>
        /// <returns></returns>
        private static bool ContainBoneInChild(Transform objTrans, ref ReadOnlyCollection<Transform> boneList)
        {
            var childTransforms = objTrans.GetComponentsInChildren<Transform>();
            foreach (var childTrans in childTransforms)
            {
                if (boneList.Contains(childTrans))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// ボーンの木構造のデータからリストデータに変換
        /// </summary>
        /// <param name="rootBoneList"></param>
        /// <returns></returns>
        private static List<BoneInfo> ConvertBoneTreeToBoneList(List<BoneInfo> rootBoneList)
        {
            var boneList = new List<BoneInfo>();
            foreach (var bone in rootBoneList)
            {
                BoneTreeToBoneList(bone, ref boneList);
            }

            return boneList;
        }

        /// <summary>
        /// ボーンの木構造のデータをリストデータに変換（再帰用）
        /// </summary>
        /// <param name="boneTree"></param>
        /// <param name="boneList"></param>
        private static void BoneTreeToBoneList(BoneInfo boneTree, ref List<BoneInfo> boneList)
        {
            if (boneTree.childs != null)
            {
                foreach (var boneInfo in boneTree.childs)
                {
                    BoneTreeToBoneList(boneInfo, ref boneList);
                }
            }

            boneTree.childs = null;
            boneList.Add(boneTree);
        }

        public class BoneTreeElement : TreeViewItem
        {
            public int Id { get; set; }
            public string Name { get; set; }
            private List<BoneTreeElement> _children = new List<BoneTreeElement>();
            public List<BoneTreeElement> Children { get { return _children; } }
            public BoneInfo Bone { get; set; }

            public void AddChild(BoneTreeElement child)
            {
                Children.Add(child);
            }
        }

        public class BoneTreeView : TreeView
        {
            private BoneTreeElement[] _baseElements;

            public BoneTreeView(TreeViewState treeViewState) : base(treeViewState)
            {
            }

            public void Setup(BoneTreeElement[] baseElements)
            {
                _baseElements = baseElements;
                Reload();
            }

            protected override TreeViewItem BuildRoot()
            {
                var root = new TreeViewItem { id = 0, depth = -1, displayName = "Root" };

                foreach (var baseElement in _baseElements)
                {
                    var baseItem = CreateTreeViewItem(baseElement);
                    root.AddChild(baseItem);
                    AddChildrenRecursive(baseElement, baseItem);
                }

                SetupDepthsFromParentsAndChildren(root);

                return root;
            }

            protected override void RowGUI(RowGUIArgs args)
            {
                var boneElement = args.item as BoneTreeElement;

                if (boneElement != null && boneElement.Bone != null)
                {
                    Rect toggleRect = args.rowRect;
                    toggleRect.x += GetContentIndent(args.item);

                    using (var check = new EditorGUI.ChangeCheckScope())
                    {
                        boneElement.Bone.deleted = GUI.Toggle(toggleRect, boneElement.Bone.deleted, args.item.displayName);
                        if (check.changed)
                            boneElement.Bone.ChangeChildDeletedState(boneElement.Bone.deleted);
                    }
                }
                else
                {
                    base.RowGUI(args);
                }

            }

            private void AddChildrenRecursive(BoneTreeElement element, TreeViewItem item)
            {
                foreach (var childElement in element.Children)
                {
                    var childItem = CreateTreeViewItem(childElement);
                    item.AddChild(childItem);
                    AddChildrenRecursive(childElement, childItem);
                }
            }

            private TreeViewItem CreateTreeViewItem(BoneTreeElement model)
            {
                return new BoneTreeElement { id = model.Id, displayName = model.Name, Bone = model.Bone };
            }
        }

        #endregion
    }
}
