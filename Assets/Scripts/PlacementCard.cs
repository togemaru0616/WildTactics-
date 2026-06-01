using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

[RequireComponent(typeof(Image))]
public class PlacementCard : MonoBehaviour,
    IPointerDownHandler, IInitializePotentialDragHandler,
    IBeginDragHandler, IDragHandler, IEndDragHandler
{
    public AnimalType AnimalType { get; set; }

    ScrollRect _scrollRect;
    Vector2    _pressPos;
    bool       _isCardDrag;

    void Awake() => _scrollRect = GetComponentInParent<ScrollRect>();

    public void OnPointerDown(PointerEventData e)
    {
        _pressPos = e.position;
        PlacementManager.Instance?.PreviewAnimal(AnimalType);
    }

    public void OnInitializePotentialDrag(PointerEventData e)
        => _scrollRect?.OnInitializePotentialDrag(e);

    public void OnBeginDrag(PointerEventData e)
    {
        var d = e.position - _pressPos;
        _isCardDrag = d.y > Mathf.Abs(d.x) && d.y > 5f;
        if (_isCardDrag)
            PlacementManager.Instance?.BeginCardDrag(AnimalType, e.position);
        else
            _scrollRect?.OnBeginDrag(e);
    }

    public void OnDrag(PointerEventData e)
    {
        if (_isCardDrag) PlacementManager.Instance?.UpdateCardDrag(e.position);
        else             _scrollRect?.OnDrag(e);
    }

    public void OnEndDrag(PointerEventData e)
    {
        if (_isCardDrag) PlacementManager.Instance?.EndCardDrag(e.position);
        else             _scrollRect?.OnEndDrag(e);
    }
}
