using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dvit.OpenBiz.Reservation;
using Dvit.OpenBiz.Pcl;

namespace Dvit.OpenBiz.Services
{
    public class AquasenseSearchStrategies
    {
        // DefaultStrategy
        public static ReservationSearchStrategy DefaultStrategy = new ReservationSearchStrategy()
        {
            ReservationEngineName = "VariableSlotReservationEngine",

            UtcOffset = new TimeSpan(2, 0, 0),

            IsFixedDuration = false,
            MinDuration = new TimeSpan(0, 5, 0),

            DefaultBeforeExtra = new TimeSpan(0, 0, 0),
            DefaultAfterExtra = new TimeSpan(0, 5, 0),

            MinBeforeExtra = new TimeSpan(0, 0, 0),
            MinAfterExtra = new TimeSpan(0, 5, 0),

            SearchScope = SearchScope.SameDay,

            SearchBeforeRequestedDate = new TimeSpan(1, 0, 0),
            SearchAfterRequestedDate = new TimeSpan(2, 0, 0),

            RoundIntervalMode = RoundIntervalMode.RoundToNearestInterval,
            RoundInterval = new TimeSpan(0, 5, 0),

            ExtraTimeMode = ExtraTimeMode.UseMinExtraTimes,
            OpenSlotMode = OpenSlotMode.AtLeastMinReservation
        };


        public static ReservationSearchStrategy TanningCabineStrategy = new ReservationSearchStrategy()
        {
            ReservationEngineName = "VariableSlotReservationEngine",

            UtcOffset = new TimeSpan(2,0,0),
            
            IsFixedDuration = false,
            MinDuration = new TimeSpan(0, 5, 0),

            DefaultBeforeExtra = new TimeSpan(0, 0, 0),
            DefaultAfterExtra = new TimeSpan(0, 5, 0),

            MinBeforeExtra = new TimeSpan(0, 0, 0),
            MinAfterExtra = new TimeSpan(0, 5, 0),

            SearchScope = SearchScope.SameDay,

            SearchBeforeRequestedDate = new TimeSpan(1, 0, 0),
            SearchAfterRequestedDate = new TimeSpan(2, 0, 0),

            RoundIntervalMode = RoundIntervalMode.RoundToNearestInterval,
            RoundInterval = new TimeSpan(0,5,0),

            ExtraTimeMode = ExtraTimeMode.UseMinExtraTimes,
            OpenSlotMode = OpenSlotMode.AtLeastMinReservation
        };

        public static ReservationSearchStrategy BodyslimmingStrategy = new ReservationSearchStrategy()
        {
            ReservationEngineName = "FixedSlotReservationEngine",
            UtcOffset = new TimeSpan(2, 0, 0),
            
            NrOfResources = 2,

            IsFixedDuration = true,
            DefaultDuration = new TimeSpan(0, 45, 0),

            DefaultBeforeExtra = new TimeSpan(0, 0, 0),
            DefaultAfterExtra = new TimeSpan(0, 0, 0),

            SearchScope = SearchScope.SameDay,

            MinBeforeExtra = new TimeSpan(0, 0, 0),
            MinAfterExtra = new TimeSpan(0, 0, 0),

            SearchBeforeRequestedDate = new TimeSpan(1, 0, 0),
            SearchAfterRequestedDate = new TimeSpan(3, 0, 0),
            
            RoundIntervalMode = RoundIntervalMode.RoundToNearestInterval,
            RoundInterval = new TimeSpan(0, 5, 0),

            ExtraTimeMode = ExtraTimeMode.UseMinExtraTimes,
            OpenSlotMode = OpenSlotMode.AtLeastMinReservation
        };

    }
}
