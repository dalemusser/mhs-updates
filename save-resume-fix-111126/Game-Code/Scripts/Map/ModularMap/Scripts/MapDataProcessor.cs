using Obvious.Soap;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MHS
{
    public class MapDataProcessor : MonoBehaviour
    {
        [SerializeField] private SOMapData _mapData;
        [Header("Map Image Attributes")]
        [SerializeField] private Image _mapImage;
        [SerializeField] private Image _fowImage;
        [SerializeField] private TextMeshProUGUI _firstHeaderText;
        [SerializeField] private TextMeshProUGUI _secondHeaderText;

        [Header("Legend Attributes")]
        [SerializeField] private GameObject _legendElementPrefab;
        [SerializeField] private Transform _legendParent;

        [Header("Other")]
        [SerializeField] private BoolVariable _fowIsActive;

        private List<MapImageData> _mapList = new List<MapImageData>();
        private List<MapFOWData> _fowList = new List<MapFOWData>();

        private void Awake()
        {
            _mapList = _mapData.GetMaps();
            _fowList = _mapData.GetFOWMaps();

            SelectMapImage();
            GenerateLegend();
        }

        #region Private Functions
        private void SelectMapImage()
        {
            if (_mapData.SetFOWOnStart && _fowList.Count > 0)
            {
                _fowImage.sprite = _fowList[0].GetFOWSprite();
                _fowIsActive.Value = true; // set, not toggle: the variable is not reset between scene loads
            }
            _mapImage.sprite = _mapList[0].GetMapSprite();
            _firstHeaderText.text = _mapData.GetWorldName();
            _secondHeaderText.text = _mapData.GetAreaName();
        }
        private void GenerateLegend()
        {
            var legendElements = _legendParent.GetComponentsInChildren<MapLegendSelector>();
            for (int i = 0; i < legendElements.Length; i++)
            {
                legendElements[i].gameObject.SetActive(false);
            }
            for (int i = 1; i < _mapList.Count; i++) //skips the first index (which is the base map)
            {
                //GameObject legendElement = Instantiate(_legendElementPrefab, _legendParent);
                var legendElement = legendElements[i].gameObject;
                legendElement.gameObject.SetActive(true);

                legendElement.GetComponentInChildren<TextMeshProUGUI>().text = _mapList[i].GetName();
                legendElement.GetComponentInChildren<Image>().sprite = _mapList[i].GetLegendSprite();
                legendElement.GetComponent<EventIntPassthrough>().SetIntPassthrough(i);
                legendElement.name = _mapList[i].GetName().Replace(" ", "").Replace("\n", " ");
                legendElement.GetComponent<MapLegendSelector>().SetReferences();
            }
            //Create different Spacing based on different Legends
            _legendParent.GetComponent<VerticalLayoutGroup>().spacing = _mapData.GetSpacing();
        }

        #endregion

        #region Public Functions

        public SOMapData GetMapData()
        {
            return _mapData;
        }

        #endregion
    }
}
